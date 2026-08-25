using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TradingAgent.Persistence;

public sealed partial class SqliteTradingRepository
{
    public async Task<IReadOnlyList<AutomationCampaignRecord>> GetAutomationCampaignsAsync(
        bool openOnly = true, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = CampaignSelect + (openOnly ? " WHERE closed_utc IS NULL" : "")
            + " ORDER BY updated_utc DESC";

        var rows = new List<AutomationCampaignRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(ReadCampaign(reader));
        return rows;
    }

    public async Task<AutomationCampaignRecord?> GetAutomationCampaignAsync(
        string symbol, bool openOnly = true, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = CampaignSelect
            + " WHERE symbol = $symbol"
            + (openOnly ? " AND closed_utc IS NULL" : "")
            + " ORDER BY updated_utc DESC LIMIT 1";
        command.Parameters.AddWithValue("$symbol", symbol.Trim().ToUpperInvariant());
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadCampaign(reader) : null;
    }

    public async Task<bool> SaveAutomationCampaignAsync(
        AutomationCampaignRecord campaign,
        long? expectedVersion = null,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = expectedVersion is null
            ? """
              INSERT INTO automation_campaigns
                (campaign_id, symbol, profile_id, profile_json, state, origin,
                 planned_budget_pkr, deployed_pkr, max_legs, completed_legs, quantity,
                 average_price, last_fill_price, current_stop, high_water_price, next_add_price,
                 status_message, started_utc, updated_utc, closed_utc, version)
              VALUES
                ($id, $symbol, $profile, $profileJson, $state, $origin,
                 $budget, $deployed, $maxLegs, $completedLegs, $quantity,
                 $average, $lastFill, $stop, $high, $nextAdd,
                 $message, $started, $updated, $closed, 1)
              ON CONFLICT(campaign_id) DO UPDATE SET
                profile_id=excluded.profile_id, profile_json=excluded.profile_json,
                state=excluded.state, origin=excluded.origin,
                planned_budget_pkr=excluded.planned_budget_pkr, deployed_pkr=excluded.deployed_pkr,
                max_legs=excluded.max_legs, completed_legs=excluded.completed_legs,
                quantity=excluded.quantity, average_price=excluded.average_price,
                last_fill_price=excluded.last_fill_price, current_stop=excluded.current_stop,
                high_water_price=excluded.high_water_price, next_add_price=excluded.next_add_price,
                status_message=excluded.status_message, updated_utc=excluded.updated_utc,
                closed_utc=excluded.closed_utc, version=automation_campaigns.version + 1
              """
            : """
              UPDATE automation_campaigns SET
                profile_id=$profile, profile_json=$profileJson, state=$state, origin=$origin,
                planned_budget_pkr=$budget, deployed_pkr=$deployed, max_legs=$maxLegs,
                completed_legs=$completedLegs, quantity=$quantity, average_price=$average,
                last_fill_price=$lastFill, current_stop=$stop, high_water_price=$high,
                next_add_price=$nextAdd, status_message=$message, updated_utc=$updated,
                closed_utc=$closed, version=version + 1
              WHERE campaign_id=$id AND version=$expected
              """;
        BindCampaign(command, campaign);
        if (expectedVersion is not null) command.Parameters.AddWithValue("$expected", expectedVersion.Value);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task AppendAutomationCampaignEventAsync(
        AutomationCampaignEventRecord item, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO automation_campaign_events
                (campaign_id, symbol, kind, message, detail_json, utc)
            VALUES ($campaign, $symbol, $kind, $message, $detail, $utc)
            """;
        command.Parameters.AddWithValue("$campaign", item.CampaignId);
        command.Parameters.AddWithValue("$symbol", item.Symbol.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("$kind", item.Kind);
        command.Parameters.AddWithValue("$message", item.Message);
        command.Parameters.AddWithValue("$detail", (object?)item.DetailJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$utc", item.Utc.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<AutomationCampaignEventRecord>> GetAutomationCampaignEventsAsync(
        string? symbol = null, string? campaignId = null, int limit = 100,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(symbol))
        {
            where.Add("symbol=$symbol");
            command.Parameters.AddWithValue("$symbol", symbol.Trim().ToUpperInvariant());
        }
        if (!string.IsNullOrWhiteSpace(campaignId))
        {
            where.Add("campaign_id=$campaign");
            command.Parameters.AddWithValue("$campaign", campaignId);
        }
        command.CommandText = """
            SELECT sequence, campaign_id, symbol, kind, message, detail_json, utc
            FROM automation_campaign_events
            """ + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
            + " ORDER BY sequence DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

        var rows = new List<AutomationCampaignEventRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new AutomationCampaignEventRecord(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
                ParseUtc(reader.GetString(6))));
        }
        return rows;
    }

    public async Task<IReadOnlyList<AutomationStrategyAssignmentRecord>>
        GetAutomationStrategyAssignmentsAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT symbol, profile_id, overrides_json, updated_utc
            FROM automation_strategy_assignments ORDER BY symbol
            """;
        var rows = new List<AutomationStrategyAssignmentRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new AutomationStrategyAssignmentRecord(
                reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2), ParseUtc(reader.GetString(3))));
        }
        return rows;
    }

    public async Task SaveAutomationStrategyAssignmentAsync(
        AutomationStrategyAssignmentRecord assignment, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO automation_strategy_assignments(symbol, profile_id, overrides_json, updated_utc)
            VALUES($symbol, $profile, $overrides, $updated)
            ON CONFLICT(symbol) DO UPDATE SET profile_id=excluded.profile_id,
                overrides_json=excluded.overrides_json, updated_utc=excluded.updated_utc
            """;
        command.Parameters.AddWithValue("$symbol", assignment.Symbol.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("$profile", assignment.ProfileId);
        command.Parameters.AddWithValue("$overrides", (object?)assignment.OverridesJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", assignment.UpdatedUtc.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAutomationStrategyAssignmentAsync(
        string symbol, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM automation_strategy_assignments WHERE symbol=$symbol";
        command.Parameters.AddWithValue("$symbol", symbol.Trim().ToUpperInvariant());
        await command.ExecuteNonQueryAsync(ct);
    }

    private const string CampaignSelect = """
        SELECT campaign_id, symbol, profile_id, profile_json, state, origin,
               planned_budget_pkr, deployed_pkr, max_legs, completed_legs, quantity,
               average_price, last_fill_price, current_stop, high_water_price, next_add_price,
               status_message, started_utc, updated_utc, closed_utc, version
        FROM automation_campaigns
        """;

    private static AutomationCampaignRecord ReadCampaign(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetString(4), reader.GetString(5), ParseDecimal(reader, 6),
        ParseDecimal(reader, 7) ?? 0m, reader.GetInt32(8), reader.GetInt32(9), reader.GetInt32(10),
        ParseDecimal(reader, 11), ParseDecimal(reader, 12), ParseDecimal(reader, 13),
        ParseDecimal(reader, 14), ParseDecimal(reader, 15),
        reader.IsDBNull(16) ? null : reader.GetString(16), ParseUtc(reader.GetString(17)),
        ParseUtc(reader.GetString(18)), reader.IsDBNull(19) ? null : ParseUtc(reader.GetString(19)),
        reader.GetInt64(20));

    private static void BindCampaign(SqliteCommand command, AutomationCampaignRecord campaign)
    {
        static object DecimalValue(decimal? value) =>
            value is null ? DBNull.Value : value.Value.ToString(CultureInfo.InvariantCulture);
        command.Parameters.AddWithValue("$id", campaign.CampaignId);
        command.Parameters.AddWithValue("$symbol", campaign.Symbol.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("$profile", campaign.ProfileId);
        command.Parameters.AddWithValue("$profileJson", campaign.ProfileJson);
        command.Parameters.AddWithValue("$state", campaign.State);
        command.Parameters.AddWithValue("$origin", campaign.Origin);
        command.Parameters.AddWithValue("$budget", DecimalValue(campaign.PlannedBudgetPkr));
        command.Parameters.AddWithValue("$deployed", DecimalValue(campaign.DeployedPkr));
        command.Parameters.AddWithValue("$maxLegs", campaign.MaxLegs);
        command.Parameters.AddWithValue("$completedLegs", campaign.CompletedLegs);
        command.Parameters.AddWithValue("$quantity", campaign.Quantity);
        command.Parameters.AddWithValue("$average", DecimalValue(campaign.AveragePrice));
        command.Parameters.AddWithValue("$lastFill", DecimalValue(campaign.LastFillPrice));
        command.Parameters.AddWithValue("$stop", DecimalValue(campaign.CurrentStop));
        command.Parameters.AddWithValue("$high", DecimalValue(campaign.HighWaterPrice));
        command.Parameters.AddWithValue("$nextAdd", DecimalValue(campaign.NextAddPrice));
        command.Parameters.AddWithValue("$message", (object?)campaign.StatusMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$started", campaign.StartedUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated", campaign.UpdatedUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$closed", campaign.ClosedUtc is null
            ? DBNull.Value : campaign.ClosedUtc.Value.ToString("O", CultureInfo.InvariantCulture));
    }
}
