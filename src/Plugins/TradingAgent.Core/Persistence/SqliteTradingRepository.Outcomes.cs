using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TradingAgent.Persistence;

/// <summary>
/// The outcome ledger: what finished campaigns actually produced, plus the two small aggregates that
/// outlive the rows.
///
/// <para>
/// <b>The retention shape is the design.</b> Raw outcomes are bounded by age and by row count.
/// Everything a longer-run judgement needs is folded into <c>automation_outcome_daily</c> at write
/// time, never derived by querying rows later — deriving it later would mean pruning rows silently
/// destroys the history, which is exactly the trap a retention policy is supposed to avoid.
/// </para>
/// </summary>
public sealed partial class SqliteTradingRepository
{
    public async Task<bool> SaveAutomationOutcomeAsync(
        AutomationOutcomeRecord outcome, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        // Idempotent on campaign_id. A campaign observed as closed again after a restart must not be
        // counted twice — and because the rollup is incremented in the same transaction, guarding the
        // insert alone is enough to keep the aggregate correct too.
        var insert = connection.CreateCommand();
        insert.Transaction = (SqliteTransaction)transaction;
        insert.CommandText = """
            INSERT INTO automation_outcomes
                (campaign_id, symbol, profile_id, entry_strategy_id, exit_plan_id, mode, simulated,
                 opened_utc, closed_utc, sessions_held, planned_entry, planned_stop, planned_target,
                 initial_risk_per_share, quantity, deployed_pkr, average_cost, realised_net_pkr,
                 realised_r, close_reason, regime_at_entry, recorded_utc)
            VALUES
                ($campaign, $symbol, $profile, $entry, $exit, $mode, $simulated,
                 $opened, $closed, $sessions, $plannedEntry, $plannedStop, $plannedTarget,
                 $risk, $quantity, $deployed, $average, $net,
                 $r, $reason, $regime, $recorded)
            ON CONFLICT(campaign_id) DO NOTHING
            """;
        insert.Parameters.AddWithValue("$campaign", outcome.CampaignId);
        insert.Parameters.AddWithValue("$symbol", outcome.Symbol.Trim().ToUpperInvariant());
        insert.Parameters.AddWithValue("$profile", outcome.ProfileId);
        insert.Parameters.AddWithValue("$entry", (object?)outcome.EntryStrategyId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$exit", (object?)outcome.ExitPlanId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$mode", outcome.Mode);
        insert.Parameters.AddWithValue("$simulated", outcome.Simulated ? 1 : 0);
        insert.Parameters.AddWithValue("$opened", Utc(outcome.OpenedUtc));
        insert.Parameters.AddWithValue("$closed", Utc(outcome.ClosedUtc));
        insert.Parameters.AddWithValue("$sessions", outcome.SessionsHeld);
        insert.Parameters.AddWithValue("$plannedEntry", Money(outcome.PlannedEntry));
        insert.Parameters.AddWithValue("$plannedStop", Money(outcome.PlannedStop));
        insert.Parameters.AddWithValue("$plannedTarget", Money(outcome.PlannedTarget));
        insert.Parameters.AddWithValue("$risk", Money(outcome.InitialRiskPerShare));
        insert.Parameters.AddWithValue("$quantity", outcome.Quantity);
        insert.Parameters.AddWithValue("$deployed", Money(outcome.DeployedPkr));
        insert.Parameters.AddWithValue("$average", Money(outcome.AverageCost));
        insert.Parameters.AddWithValue("$net", Money(outcome.RealisedNetPkr));
        insert.Parameters.AddWithValue("$r", Money(outcome.RealisedR));
        insert.Parameters.AddWithValue("$reason", outcome.CloseReason);
        insert.Parameters.AddWithValue("$regime", (object?)outcome.RegimeAtEntry ?? DBNull.Value);
        insert.Parameters.AddWithValue("$recorded", Utc(outcome.RecordedUtc));

        if (await insert.ExecuteNonQueryAsync(ct) != 1)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }

        // A null net result is neither a win nor a loss. Counting it as a loss would defame a trade
        // whose result was merely unreadable, and counting it as a win is worse; Trades still rises,
        // so the gap stays visible as Trades exceeding Wins + Losses.
        var win = outcome.RealisedNetPkr is > 0m ? 1 : 0;
        var loss = outcome.RealisedNetPkr is < 0m ? 1 : 0;
        var measured = outcome.RealisedR is not null ? 1 : 0;

        var rollup = connection.CreateCommand();
        rollup.Transaction = (SqliteTransaction)transaction;
        rollup.CommandText = """
            INSERT INTO automation_outcome_daily
                (day, profile_id, mode, trades, wins, losses, measured, sum_r, sum_net_pkr, updated_utc)
            VALUES ($day, $profile, $mode, 1, $win, $loss, $measured, $r, $net, $updated)
            ON CONFLICT(day, profile_id, mode) DO UPDATE SET
                trades      = automation_outcome_daily.trades + 1,
                wins        = automation_outcome_daily.wins + $win,
                losses      = automation_outcome_daily.losses + $loss,
                measured    = automation_outcome_daily.measured + $measured,
                sum_r       = CAST(automation_outcome_daily.sum_r AS REAL) + CAST($r AS REAL),
                sum_net_pkr = CAST(automation_outcome_daily.sum_net_pkr AS REAL) + CAST($net AS REAL),
                updated_utc = excluded.updated_utc
            """;
        rollup.Parameters.AddWithValue("$day", Day(outcome.ClosedUtc));
        rollup.Parameters.AddWithValue("$profile", outcome.ProfileId);
        rollup.Parameters.AddWithValue("$mode", outcome.Mode);
        rollup.Parameters.AddWithValue("$win", win);
        rollup.Parameters.AddWithValue("$loss", loss);
        rollup.Parameters.AddWithValue("$measured", measured);
        // Unknown contributes nothing to a sum. The Measured count is what stops that being read as
        // a zero-R trade when an average is taken.
        rollup.Parameters.AddWithValue("$r", Sum(outcome.RealisedR));
        rollup.Parameters.AddWithValue("$net", Sum(outcome.RealisedNetPkr));
        rollup.Parameters.AddWithValue("$updated", Utc(outcome.RecordedUtc));
        await rollup.ExecuteNonQueryAsync(ct);

        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<AutomationOutcomeRecord>> GetAutomationOutcomesAsync(
        string? symbol = null, string? profileId = null, int limit = 100,
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
        if (!string.IsNullOrWhiteSpace(profileId))
        {
            where.Add("profile_id=$profile");
            command.Parameters.AddWithValue("$profile", profileId);
        }

        command.CommandText = OutcomeSelect
            + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
            + " ORDER BY closed_utc DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));

        var rows = new List<AutomationOutcomeRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(ReadOutcome(reader));
        return rows;
    }

    public async Task<IReadOnlyList<AutomationOutcomeDailyRecord>> GetAutomationOutcomeDailyAsync(
        string? sinceDay = null, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT day, profile_id, mode, trades, wins, losses, measured, sum_r, sum_net_pkr, updated_utc
            FROM automation_outcome_daily
            """
            + (string.IsNullOrWhiteSpace(sinceDay) ? "" : " WHERE day >= $since")
            + " ORDER BY day DESC, profile_id, mode";
        if (!string.IsNullOrWhiteSpace(sinceDay))
            command.Parameters.AddWithValue("$since", sinceDay);

        var rows = new List<AutomationOutcomeDailyRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new AutomationOutcomeDailyRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6),
                ParseDecimal(reader, 7) ?? 0m, ParseDecimal(reader, 8) ?? 0m,
                ParseUtc(reader.GetString(9))));
        }
        return rows;
    }

    public async Task AddAutomationGateRejectionsAsync(
        string day,
        string strategyId,
        IReadOnlyDictionary<string, int> countsByGateCode,
        CancellationToken ct = default)
    {
        if (countsByGateCode.Count == 0) return;

        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        foreach (var (code, count) in countsByGateCode)
        {
            if (count <= 0) continue;
            var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO automation_gate_rejections (day, strategy_id, gate_code, count, updated_utc)
                VALUES ($day, $strategy, $code, $count, $updated)
                ON CONFLICT(day, strategy_id, gate_code) DO UPDATE SET
                    count = automation_gate_rejections.count + $count,
                    updated_utc = excluded.updated_utc
                """;
            command.Parameters.AddWithValue("$day", day);
            command.Parameters.AddWithValue("$strategy", strategyId);
            command.Parameters.AddWithValue("$code", code);
            command.Parameters.AddWithValue("$count", count);
            command.Parameters.AddWithValue("$updated", Utc(DateTime.UtcNow));
            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<AutomationGateRejectionRecord>> GetAutomationGateRejectionsAsync(
        string? sinceDay = null, string? strategyId = null, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();

        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(sinceDay))
        {
            where.Add("day >= $since");
            command.Parameters.AddWithValue("$since", sinceDay);
        }
        if (!string.IsNullOrWhiteSpace(strategyId))
        {
            where.Add("strategy_id = $strategy");
            command.Parameters.AddWithValue("$strategy", strategyId);
        }

        command.CommandText = """
            SELECT day, strategy_id, gate_code, count, updated_utc
            FROM automation_gate_rejections
            """
            + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
            + " ORDER BY count DESC, gate_code";

        var rows = new List<AutomationGateRejectionRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new AutomationGateRejectionRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt32(3), ParseUtc(reader.GetString(4))));
        }
        return rows;
    }

    public async Task<(int Outcomes, int Daily, int GateRejections)> PruneAutomationOutcomesAsync(
        int outcomeRetentionDays,
        int outcomeMaxRows,
        int dailyRetentionDays,
        int gateRejectionRetentionDays,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var now = DateTime.UtcNow;

        // Age first, then count. Doing it the other way round would let a burst of activity keep rows
        // that are past their retention date simply because the table was under its row budget.
        var byAge = connection.CreateCommand();
        byAge.Transaction = (SqliteTransaction)transaction;
        byAge.CommandText = "DELETE FROM automation_outcomes WHERE closed_utc < $cutoff";
        byAge.Parameters.AddWithValue(
            "$cutoff", Utc(now.AddDays(-Math.Max(1, outcomeRetentionDays))));
        var outcomes = await byAge.ExecuteNonQueryAsync(ct);

        var byCount = connection.CreateCommand();
        byCount.Transaction = (SqliteTransaction)transaction;
        byCount.CommandText = """
            DELETE FROM automation_outcomes
            WHERE campaign_id NOT IN (
                SELECT campaign_id FROM automation_outcomes
                ORDER BY closed_utc DESC LIMIT $keep)
            """;
        byCount.Parameters.AddWithValue("$keep", Math.Max(1, outcomeMaxRows));
        outcomes += await byCount.ExecuteNonQueryAsync(ct);

        var daily = connection.CreateCommand();
        daily.Transaction = (SqliteTransaction)transaction;
        daily.CommandText = "DELETE FROM automation_outcome_daily WHERE day < $cutoff";
        daily.Parameters.AddWithValue(
            "$cutoff", Day(now.AddDays(-Math.Max(1, dailyRetentionDays))));
        var dailyDeleted = await daily.ExecuteNonQueryAsync(ct);

        var gates = connection.CreateCommand();
        gates.Transaction = (SqliteTransaction)transaction;
        gates.CommandText = "DELETE FROM automation_gate_rejections WHERE day < $cutoff";
        gates.Parameters.AddWithValue(
            "$cutoff", Day(now.AddDays(-Math.Max(1, gateRejectionRetentionDays))));
        var gatesDeleted = await gates.ExecuteNonQueryAsync(ct);

        await transaction.CommitAsync(ct);
        return (outcomes, dailyDeleted, gatesDeleted);
    }

    private const string OutcomeSelect = """
        SELECT campaign_id, symbol, profile_id, entry_strategy_id, exit_plan_id, mode, simulated,
               opened_utc, closed_utc, sessions_held, planned_entry, planned_stop, planned_target,
               initial_risk_per_share, quantity, deployed_pkr, average_cost, realised_net_pkr,
               realised_r, close_reason, regime_at_entry, recorded_utc
        FROM automation_outcomes
        """;

    private static AutomationOutcomeRecord ReadOutcome(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.GetString(5), reader.GetInt32(6) != 0,
        ParseUtc(reader.GetString(7)), ParseUtc(reader.GetString(8)), reader.GetInt32(9),
        ParseDecimal(reader, 10), ParseDecimal(reader, 11), ParseDecimal(reader, 12),
        ParseDecimal(reader, 13), reader.GetInt32(14), ParseDecimal(reader, 15) ?? 0m,
        ParseDecimal(reader, 16), ParseDecimal(reader, 17), ParseDecimal(reader, 18),
        reader.GetString(19), reader.IsDBNull(20) ? null : reader.GetString(20),
        ParseUtc(reader.GetString(21)));

    private static string Utc(DateTime value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>The rollup's bucket key. UTC throughout, so a day never shifts under a reader.</summary>
    private static string Day(DateTime value) =>
        value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// A value being added into a running total. Unknown contributes <b>zero to the sum</b> while the
    /// separate Measured count records that it was never a measurement — the only way to keep an
    /// average honest without letting an unknown masquerade as a flat result.
    /// </summary>
    private static string Sum(decimal? value) =>
        Math.Round(value ?? 0m, 4, MidpointRounding.AwayFromZero)
            .ToString(CultureInfo.InvariantCulture);
}
