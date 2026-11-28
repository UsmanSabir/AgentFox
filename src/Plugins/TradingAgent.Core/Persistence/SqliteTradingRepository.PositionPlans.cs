using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradingAgent.Watchlist;

namespace TradingAgent.Persistence;

/// <summary>Position plans (<see cref="IPositionPlanStore"/>).</summary>
public sealed partial class SqliteTradingRepository : IPositionPlanStore
{
    private static readonly JsonSerializerOptions PlanJson = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<PositionPlan>> GetPositionPlansAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT plan_json, version, updated_utc, updated_by FROM position_plans ORDER BY symbol";
        return await ReadPlansAsync(command, ct);
    }

    public async Task<PositionPlan?> GetPositionPlanAsync(string symbol, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT plan_json, version, updated_utc, updated_by FROM position_plans WHERE symbol = $s";
        command.Parameters.AddWithValue("$s", Normalize(symbol));
        return (await ReadPlansAsync(command, ct)).SingleOrDefault();
    }

    public async Task<PositionPlan?> TrySavePositionPlanAsync(PositionPlan plan, int expectedVersion, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var symbol = Normalize(plan.Symbol);
        var saved = plan with { Symbol = symbol, Version = expectedVersion + 1, UpdatedUtc = DateTime.UtcNow };
        var json = JsonSerializer.Serialize(saved, PlanJson);

        var command = connection.CreateCommand();
        // One statement for both cases, so two concurrent writers cannot both pass a separate version check.
        command.CommandText = expectedVersion == 0
            ? """
              INSERT INTO position_plans (symbol, plan_json, version, updated_utc, updated_by)
              VALUES ($s, $json, 1, $at, $by)
              ON CONFLICT(symbol) DO NOTHING
              """
            : """
              UPDATE position_plans SET plan_json = $json, version = version + 1, updated_utc = $at, updated_by = $by
               WHERE symbol = $s AND version = $expected
              """;
        command.Parameters.AddWithValue("$s", symbol);
        command.Parameters.AddWithValue("$json", json);
        command.Parameters.AddWithValue("$at", saved.UpdatedUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$by", (object?)plan.UpdatedBy ?? DBNull.Value);
        command.Parameters.AddWithValue("$expected", expectedVersion);
        return await command.ExecuteNonQueryAsync(ct) == 1 ? saved : null;
    }

    public async Task<bool> DeletePositionPlanAsync(string symbol, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM position_plans WHERE symbol = $s";
        command.Parameters.AddWithValue("$s", Normalize(symbol));
        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    private static string Normalize(string symbol) => symbol.Trim().ToUpperInvariant();

    private static async Task<IReadOnlyList<PositionPlan>> ReadPlansAsync(SqliteCommand command, CancellationToken ct)
    {
        var plans = new List<PositionPlan>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (JsonSerializer.Deserialize<PositionPlan>(reader.GetString(0), PlanJson) is not { } plan) continue;
            // The row's own columns are authoritative for version and audit, whatever the JSON says.
            plans.Add(plan with
            {
                Version = reader.GetInt32(1),
                UpdatedUtc = ParseUtc(reader.GetString(2)),
                UpdatedBy = reader.IsDBNull(3) ? null : reader.GetString(3)
            });
        }
        return plans;
    }
}
