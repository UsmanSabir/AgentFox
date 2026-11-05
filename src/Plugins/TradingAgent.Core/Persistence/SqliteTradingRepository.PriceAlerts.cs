using System.Text.Json;
using TradingAgent.Watchlist;

namespace TradingAgent.Persistence;

public sealed partial class SqliteTradingRepository
{
    // At most 500 rules and events together. Active rules expire after 30 days.
    // The price-alert worker sweeps terminal history after seven days, including outside market hours.
    public async Task<IReadOnlyList<PriceAlert>> ReadPriceAlertsAsync(CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT data_json FROM price_alerts ORDER BY changed_utc DESC LIMIT 500;";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var alerts = new List<PriceAlert>();
        while (await reader.ReadAsync(ct))
            alerts.Add(JsonSerializer.Deserialize<PriceAlert>(reader.GetString(0))!);
        return alerts;
    }

    public async Task<bool> WritePriceAlertAsync(PriceAlert next, PriceAlert? previous, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$id", next.Id.ToString("D"));
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(next));
        command.Parameters.AddWithValue("$changed", DateTime.UtcNow.ToString("O"));
        if (previous is null)
        {
            command.CommandText = """
                INSERT INTO price_alerts(id, data_json, changed_utc)
                SELECT $id, $json, $changed WHERE (SELECT count(*) FROM price_alerts) < 500
                AND NOT EXISTS (
                    SELECT 1 FROM price_alerts
                    WHERE json_extract(data_json, '$.State') = 'watching'
                      AND json_extract(data_json, '$.Symbol') = json_extract($json, '$.Symbol')
                      AND json_extract(data_json, '$.Direction') = json_extract($json, '$.Direction')
                      AND json_extract(data_json, '$.Target') = json_extract($json, '$.Target')
                );
                """;
        }
        else
        {
            command.CommandText = """
                UPDATE price_alerts SET data_json=$json, changed_utc=$changed
                WHERE id=$id AND data_json=$previous;
                """;
            command.Parameters.AddWithValue("$previous", JsonSerializer.Serialize(previous));
        }
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    internal async Task PrunePriceAlertsAsync(DateTime now, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM price_alerts
            WHERE julianday(json_extract(data_json, '$.FinishedUtc')) <= julianday($cutoff)
              AND json_extract(data_json, '$.State') != 'pending';
            """;
        command.Parameters.AddWithValue("$cutoff", now.AddDays(-7).ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }
}
