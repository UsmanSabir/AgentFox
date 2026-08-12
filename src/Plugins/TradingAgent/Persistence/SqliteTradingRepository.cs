using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.Json;
using TradingAgent.Analysis;
using TradingAgent.Config;
using TradingAgent.Manager;
using TradingAgent.Reconciliation;
using TradingAgent.Watchlist;

namespace TradingAgent.Persistence;

/// <summary>
/// Transactional operational ledger. A unique idempotency key is claimed before broker access;
/// the recorded result is returned on replay instead of submitting another order.
/// </summary>
public sealed class SqliteTradingRepository : ITradingRepository
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteTradingRepository> _logger;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private volatile bool _initialized;

    public SqliteTradingRepository(
        IOptions<TradingAgentOptions> options,
        IConfiguration configuration,
        ILogger<SqliteTradingRepository> logger)
    {
        _logger = logger;
        var configured = options.Value.DatabasePath;
        var root = ResolveWorkspaceRoot(configuration);
        var path = Path.IsPathRooted(configured) ? configured : Path.Combine(root, configured);
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString();
    }

    public async Task<ExecutionClaim> TryBeginExecutionAsync(
        string idempotencyKey,
        string requestJson,
        string policyVersion,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var executionId = Guid.NewGuid().ToString("N");
        var insert = connection.CreateCommand();
        insert.Transaction = (SqliteTransaction)transaction;
        insert.CommandText = """
            INSERT OR IGNORE INTO trading_executions
                (execution_id, idempotency_key, state, request_json, policy_version, created_utc, updated_utc)
            VALUES
                ($id, $key, 'submitting', $request, $policy, $now, $now)
            """;
        insert.Parameters.AddWithValue("$id", executionId);
        insert.Parameters.AddWithValue("$key", idempotencyKey);
        insert.Parameters.AddWithValue("$request", requestJson);
        insert.Parameters.AddWithValue("$policy", policyVersion);
        insert.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        var inserted = await insert.ExecuteNonQueryAsync(ct) == 1;

        var selected = connection.CreateCommand();
        selected.Transaction = (SqliteTransaction)transaction;
        selected.CommandText =
            "SELECT execution_id, state, result_json FROM trading_executions WHERE idempotency_key = $key";
        selected.Parameters.AddWithValue("$key", idempotencyKey);
        await using var reader = await selected.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException("Execution idempotency claim could not be read after insert.");
        var claim = new ExecutionClaim(
            inserted,
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
        await reader.DisposeAsync();
        await transaction.CommitAsync(ct);
        return claim;
    }

    public async Task<string> CreateProposalAsync(
        string idempotencyKey,
        string proposalJson,
        string policyVersion,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var proposalId = Guid.NewGuid().ToString("N");
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO trade_proposals
                (proposal_id, idempotency_key, status, proposal_json, policy_version, created_utc, updated_utc)
            VALUES ($id, $key, 'proposed', $json, $policy, $now, $now);
            SELECT proposal_id FROM trade_proposals WHERE idempotency_key = $key;
            """;
        command.Parameters.AddWithValue("$id", proposalId);
        command.Parameters.AddWithValue("$key", idempotencyKey);
        command.Parameters.AddWithValue("$json", proposalJson);
        command.Parameters.AddWithValue("$policy", policyVersion);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        return (string)(await command.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("Proposal was not persisted."));
    }

    public async Task<TradingLedgerStatus> GetStatusAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM trade_proposals WHERE status IN ('proposed','awaiting_approval')),
                (SELECT COUNT(*) FROM trading_executions WHERE state = 'submitting'),
                (SELECT COUNT(*) FROM trading_executions WHERE state = 'unknown'),
                (SELECT COUNT(*) FROM trading_executions WHERE state = 'accepted');
            """;
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new TradingLedgerStatus(
            reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), DateTime.UtcNow);
    }

    public async Task<IReadOnlyList<TradeProposalRecord>> GetProposalsAsync(
        int limit = 100,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT proposal_id, status, proposal_json, policy_version, created_utc, updated_utc
            FROM trade_proposals
            ORDER BY created_utc DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        var records = new List<TradeProposalRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            records.Add(new TradeProposalRecord(
                reader.GetString(0), reader.GetString(1), ParseJson(reader.GetString(2)),
                reader.GetString(3), ParseUtc(reader.GetString(4)), ParseUtc(reader.GetString(5))));
        }
        return records;
    }

    public async Task<IReadOnlyList<TradingExecutionRecord>> GetExecutionsAsync(
        int limit = 100,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT execution_id, state, request_json, result_json, policy_version, created_utc, updated_utc
            FROM trading_executions
            ORDER BY created_utc DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        var records = new List<TradingExecutionRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            records.Add(new TradingExecutionRecord(
                reader.GetString(0), reader.GetString(1), ParseJson(reader.GetString(2)),
                reader.IsDBNull(3) ? null : ParseJson(reader.GetString(3)), reader.GetString(4),
                ParseUtc(reader.GetString(5)), ParseUtc(reader.GetString(6))));
        }
        return records;
    }

    public async Task<IReadOnlyList<TradingEventRecord>> GetEventsAsync(
        int limit = 200,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_id, execution_id, event_type, payload_json, created_utc
            FROM trading_order_events
            ORDER BY event_id DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        var records = new List<TradingEventRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            records.Add(new TradingEventRecord(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                ParseJson(reader.GetString(3)), ParseUtc(reader.GetString(4))));
        }
        return records;
    }

    public async Task<IReadOnlyList<ReconciliationRunRecord>> GetReconciliationRunsAsync(
        int limit = 100,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT reconciliation_id, state, details_json, started_utc, completed_utc
            FROM reconciliation_runs
            ORDER BY started_utc DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        var records = new List<ReconciliationRunRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            records.Add(new ReconciliationRunRecord(
                reader.GetString(0), reader.GetString(1), ParseJson(reader.GetString(2)),
                ParseUtc(reader.GetString(3)), reader.IsDBNull(4) ? null : ParseUtc(reader.GetString(4))));
        }
        return records;
    }

    public async Task RecordReconciliationAsync(
        BrokerReconciliationSnapshot snapshot,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO reconciliation_runs
                (reconciliation_id, state, details_json, started_utc, completed_utc)
            VALUES ($id, $state, $details, $checked, $checked)
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$state", snapshot.Healthy ? "healthy" : "unhealthy");
        command.Parameters.AddWithValue("$details", snapshot.DetailsJson);
        command.Parameters.AddWithValue("$checked", snapshot.CheckedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task CompleteExecutionAsync(
        string executionId,
        string state,
        string resultJson,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE trading_executions
            SET state = $state, result_json = $result, updated_utc = $now
            WHERE execution_id = $id
            """;
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$result", resultJson);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", executionId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task AppendEventAsync(
        string executionId,
        string eventType,
        string payloadJson,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO trading_order_events (execution_id, event_type, payload_json, created_utc)
            VALUES ($id, $type, $payload, $now)
            """;
        command.Parameters.AddWithValue("$id", executionId);
        command.Parameters.AddWithValue("$type", eventType);
        command.Parameters.AddWithValue("$payload", payloadJson);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveDailySessionAsync(
        DateOnly sessionDate,
        IReadOnlyList<TradingAgent.Research.PsxCandle> bars,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var session = sessionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var nowUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        if (bars.Count > 0)
        {
            var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT INTO daily_bars
                    (symbol, session_date, open, high, low, close, previous_close, volume, saved_utc)
                VALUES
                    ($symbol, $session, $open, $high, $low, $close, $prev, $volume, $saved)
                ON CONFLICT (symbol, session_date) DO UPDATE SET
                    open = excluded.open, high = excluded.high, low = excluded.low,
                    close = excluded.close, previous_close = excluded.previous_close,
                    volume = excluded.volume, saved_utc = excluded.saved_utc
                """;

            var symbol = insert.Parameters.Add("$symbol", SqliteType.Text);
            var date = insert.Parameters.Add("$session", SqliteType.Text);
            var open = insert.Parameters.Add("$open", SqliteType.Text);
            var high = insert.Parameters.Add("$high", SqliteType.Text);
            var low = insert.Parameters.Add("$low", SqliteType.Text);
            var close = insert.Parameters.Add("$close", SqliteType.Text);
            var prev = insert.Parameters.Add("$prev", SqliteType.Text);
            var volume = insert.Parameters.Add("$volume", SqliteType.Integer);
            var saved = insert.Parameters.Add("$saved", SqliteType.Text);
            saved.Value = nowUtc;

            foreach (var bar in bars)
            {
                // A forming session is not archived: it would freeze an intraday snapshot as if it
                // were that day's settled candle.
                if (bar.IsLive || bar.IsIntraday) continue;

                symbol.Value = bar.Symbol;
                date.Value = bar.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                open.Value = bar.Open.ToString(CultureInfo.InvariantCulture);
                high.Value = bar.High.ToString(CultureInfo.InvariantCulture);
                low.Value = bar.Low.ToString(CultureInfo.InvariantCulture);
                close.Value = bar.Close.ToString(CultureInfo.InvariantCulture);
                prev.Value = bar.PreviousClose is { } p
                    ? p.ToString(CultureInfo.InvariantCulture)
                    : (object)DBNull.Value;
                volume.Value = bar.Volume;
                await insert.ExecuteNonQueryAsync(ct);
            }
        }

        var coverage = connection.CreateCommand();
        coverage.Transaction = (SqliteTransaction)transaction;
        coverage.CommandText = """
            INSERT INTO daily_bar_coverage (session_date, symbol_count, fetched_utc)
            VALUES ($session, $count, $fetched)
            ON CONFLICT (session_date) DO UPDATE SET
                symbol_count = excluded.symbol_count, fetched_utc = excluded.fetched_utc
            """;
        coverage.Parameters.AddWithValue("$session", session);
        coverage.Parameters.AddWithValue("$count", bars.Count);
        coverage.Parameters.AddWithValue("$fetched", nowUtc);
        await coverage.ExecuteNonQueryAsync(ct);

        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<TradingAgent.Research.PsxCandle>> GetDailyBarsAsync(
        string symbol,
        int maxBars,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);

        symbol = symbol.Trim().ToUpperInvariant();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_date, open, high, low, close, previous_close, volume
            FROM daily_bars
            WHERE symbol = $symbol
            ORDER BY session_date DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$symbol", symbol);
        command.Parameters.AddWithValue("$limit", Math.Clamp(maxBars, 1, 5000));

        var bars = new List<TradingAgent.Research.PsxCandle>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            bars.Add(new TradingAgent.Research.PsxCandle
            {
                Symbol        = symbol,
                Date          = DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                Open          = decimal.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                High          = decimal.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                Low           = decimal.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                Close         = decimal.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                PreviousClose = reader.IsDBNull(5)
                                    ? null
                                    : decimal.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
                Volume        = reader.GetInt64(6)
            });
        }

        bars.Reverse();
        return bars;
    }

    public async Task<IReadOnlySet<DateOnly>> GetCoveredDailyDatesAsync(
        DateOnly fromInclusive,
        DateOnly toInclusive,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_date FROM daily_bar_coverage
            WHERE session_date >= $from AND session_date <= $to
            """;
        command.Parameters.AddWithValue("$from", fromInclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$to", toInclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        var dates = new HashSet<DateOnly>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            dates.Add(DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture));

        return dates;
    }

    public async Task<int> ClearDailyCoverageAfterAsync(
        DateOnly settledThrough, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM daily_bar_coverage WHERE session_date > $through";
        command.Parameters.AddWithValue(
            "$through", settledThrough.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var removed = await command.ExecuteNonQueryAsync(ct);
        if (removed > 0)
            _logger.LogInformation(
                "[TradingLedger] Cleared {Count} unsettled daily-coverage marker(s) after {Through}; "
                + "those sessions will be fetched again once settled.", removed, settledThrough);
        return removed;
    }

    public async Task<DailyArchiveStatus> GetDailyArchiveStatusAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(DISTINCT symbol) FROM daily_bars),
                (SELECT COUNT(*) FROM daily_bars),
                (SELECT COUNT(*) FROM daily_bar_coverage),
                (SELECT MIN(session_date) FROM daily_bars),
                (SELECT MAX(session_date) FROM daily_bars)
            """;

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return new DailyArchiveStatus(0, 0, 0, null, null);

        return new DailyArchiveStatus(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.IsDBNull(3) ? null : DateOnly.ParseExact(reader.GetString(3), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            reader.IsDBNull(4) ? null : DateOnly.ParseExact(reader.GetString(4), "yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    public async Task SaveIntradayBarsAsync(
        IReadOnlyList<TradingAgent.Research.PsxCandle> bars,
        CancellationToken ct = default)
    {
        // Only settled bars are archived. Persisting the in-progress bucket would freeze a partial
        // bar into history the moment a scan happened to run mid-bucket.
        var settled = bars.Where(b => !b.IsLive && b.IsIntraday && b.BucketStartUtc is not null).ToList();
        if (settled.Count == 0) return;

        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO intraday_bars
                (symbol, interval_minutes, bucket_start_utc, session_date, open, high, low, close, volume, saved_utc)
            VALUES
                ($symbol, $interval, $start, $session, $open, $high, $low, $close, $volume, $saved)
            ON CONFLICT (symbol, interval_minutes, bucket_start_utc) DO UPDATE SET
                open = excluded.open, high = excluded.high, low = excluded.low,
                close = excluded.close, volume = excluded.volume, saved_utc = excluded.saved_utc
            """;

        var symbol = command.Parameters.Add("$symbol", SqliteType.Text);
        var interval = command.Parameters.Add("$interval", SqliteType.Integer);
        var start = command.Parameters.Add("$start", SqliteType.Text);
        var session = command.Parameters.Add("$session", SqliteType.Text);
        var open = command.Parameters.Add("$open", SqliteType.Text);
        var high = command.Parameters.Add("$high", SqliteType.Text);
        var low = command.Parameters.Add("$low", SqliteType.Text);
        var close = command.Parameters.Add("$close", SqliteType.Text);
        var volume = command.Parameters.Add("$volume", SqliteType.Integer);
        var saved = command.Parameters.Add("$saved", SqliteType.Text);
        saved.Value = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        foreach (var bar in settled)
        {
            symbol.Value = bar.Symbol;
            interval.Value = bar.IntervalMinutes;
            start.Value = bar.BucketStartUtc!.Value.ToString("O", CultureInfo.InvariantCulture);
            session.Value = bar.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            open.Value = bar.Open.ToString(CultureInfo.InvariantCulture);
            high.Value = bar.High.ToString(CultureInfo.InvariantCulture);
            low.Value = bar.Low.ToString(CultureInfo.InvariantCulture);
            close.Value = bar.Close.ToString(CultureInfo.InvariantCulture);
            volume.Value = bar.Volume;
            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<TradingAgent.Research.PsxCandle>> GetIntradayBarsAsync(
        string symbol,
        int intervalMinutes,
        int maxBars,
        DateTime? beforeUtc = null,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT bucket_start_utc, session_date, open, high, low, close, volume
            FROM intraday_bars
            WHERE symbol = $symbol
              AND interval_minutes = $interval
              AND ($before IS NULL OR bucket_start_utc < $before)
            ORDER BY bucket_start_utc DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$symbol", symbol.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("$interval", intervalMinutes);
        command.Parameters.AddWithValue("$before",
            beforeUtc is null ? DBNull.Value : beforeUtc.Value.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$limit", Math.Clamp(maxBars, 1, 5000));

        var bars = new List<TradingAgent.Research.PsxCandle>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            bars.Add(new TradingAgent.Research.PsxCandle
            {
                Symbol          = symbol.Trim().ToUpperInvariant(),
                BucketStartUtc  = DateTime.Parse(reader.GetString(0), CultureInfo.InvariantCulture,
                                      DateTimeStyles.RoundtripKind),
                Date            = DateOnly.ParseExact(reader.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                IntervalMinutes = intervalMinutes,
                Open            = decimal.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                High            = decimal.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                Low             = decimal.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                Close           = decimal.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
                Volume          = reader.GetInt64(6)
            });
        }

        // Queried newest-first for the LIMIT; the analyzers want oldest-first.
        bars.Reverse();
        return bars;
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;
        await _initializeGate.WaitAsync(ct);
        try
        {
            if (_initialized) return;
            await using var connection = await OpenAsync(ct);
            var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA foreign_keys=ON;
                CREATE TABLE IF NOT EXISTS trading_executions (
                    execution_id TEXT PRIMARY KEY,
                    idempotency_key TEXT NOT NULL UNIQUE,
                    state TEXT NOT NULL,
                    request_json TEXT NOT NULL,
                    result_json TEXT NULL,
                    policy_version TEXT NOT NULL,
                    created_utc TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS inbound_messages (
                    message_id TEXT PRIMARY KEY,
                    source TEXT NOT NULL,
                    sender_id TEXT NULL,
                    received_utc TEXT NOT NULL,
                    body_hash TEXT NOT NULL,
                    authentication_state TEXT NOT NULL,
                    payload_json TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS parsed_signals (
                    signal_id TEXT PRIMARY KEY,
                    message_id TEXT NULL,
                    signal_json TEXT NOT NULL,
                    parser_model TEXT NULL,
                    parser_version TEXT NULL,
                    created_utc TEXT NOT NULL,
                    FOREIGN KEY (message_id) REFERENCES inbound_messages(message_id)
                );
                CREATE TABLE IF NOT EXISTS trade_proposals (
                    proposal_id TEXT PRIMARY KEY,
                    idempotency_key TEXT NOT NULL UNIQUE,
                    status TEXT NOT NULL,
                    proposal_json TEXT NOT NULL,
                    policy_version TEXT NOT NULL,
                    created_utc TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS approvals (
                    approval_id TEXT PRIMARY KEY,
                    proposal_id TEXT NOT NULL,
                    proposal_hash TEXT NOT NULL,
                    actor TEXT NOT NULL,
                    decision TEXT NOT NULL,
                    decided_utc TEXT NOT NULL,
                    FOREIGN KEY (proposal_id) REFERENCES trade_proposals(proposal_id)
                );
                CREATE TABLE IF NOT EXISTS broker_orders (
                    broker_order_id TEXT PRIMARY KEY,
                    execution_id TEXT NOT NULL,
                    client_order_id TEXT NOT NULL UNIQUE,
                    state TEXT NOT NULL,
                    order_json TEXT NOT NULL,
                    created_utc TEXT NOT NULL,
                    updated_utc TEXT NOT NULL,
                    FOREIGN KEY (execution_id) REFERENCES trading_executions(execution_id)
                );
                CREATE TABLE IF NOT EXISTS fills (
                    fill_id TEXT PRIMARY KEY,
                    broker_order_id TEXT NOT NULL,
                    quantity INTEGER NOT NULL,
                    price TEXT NOT NULL,
                    filled_utc TEXT NOT NULL,
                    FOREIGN KEY (broker_order_id) REFERENCES broker_orders(broker_order_id)
                );
                CREATE TABLE IF NOT EXISTS positions (
                    account_id TEXT NOT NULL,
                    symbol TEXT NOT NULL,
                    quantity INTEGER NOT NULL,
                    average_price TEXT NOT NULL,
                    updated_utc TEXT NOT NULL,
                    PRIMARY KEY (account_id, symbol)
                );
                CREATE TABLE IF NOT EXISTS reconciliation_runs (
                    reconciliation_id TEXT PRIMARY KEY,
                    state TEXT NOT NULL,
                    details_json TEXT NOT NULL,
                    started_utc TEXT NOT NULL,
                    completed_utc TEXT NULL
                );
                CREATE TABLE IF NOT EXISTS trading_order_events (
                    event_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    execution_id TEXT NOT NULL,
                    event_type TEXT NOT NULL,
                    payload_json TEXT NOT NULL,
                    created_utc TEXT NOT NULL,
                    FOREIGN KEY (execution_id) REFERENCES trading_executions(execution_id)
                );
                CREATE TABLE IF NOT EXISTS daily_bars (
                    symbol TEXT NOT NULL,
                    session_date TEXT NOT NULL,
                    open TEXT NOT NULL,
                    high TEXT NOT NULL,
                    low TEXT NOT NULL,
                    close TEXT NOT NULL,
                    previous_close TEXT NULL,
                    volume INTEGER NOT NULL,
                    saved_utc TEXT NOT NULL,
                    PRIMARY KEY (symbol, session_date)
                );
                -- Which dates have been retrieved at all, so the backfill is resumable and a known
                -- non-trading day (symbol_count = 0) is never refetched.
                CREATE TABLE IF NOT EXISTS daily_bar_coverage (
                    session_date TEXT PRIMARY KEY,
                    symbol_count INTEGER NOT NULL,
                    fetched_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS intraday_bars (
                    symbol TEXT NOT NULL,
                    interval_minutes INTEGER NOT NULL,
                    bucket_start_utc TEXT NOT NULL,
                    session_date TEXT NOT NULL,
                    open TEXT NOT NULL,
                    high TEXT NOT NULL,
                    low TEXT NOT NULL,
                    close TEXT NOT NULL,
                    volume INTEGER NOT NULL,
                    saved_utc TEXT NOT NULL,
                    PRIMARY KEY (symbol, interval_minutes, bucket_start_utc)
                );
                -- The user's monitoring universe: seeded from AllowedSymbols but independent of it
                -- afterwards, so adding a symbol to watch never widens what may be traded (that
                -- stays AllowedSymbols, read directly by TradingRiskEngine).
                CREATE TABLE IF NOT EXISTS watchlist (
                    symbol         TEXT PRIMARY KEY,
                    added_utc      TEXT NOT NULL,
                    source         TEXT NOT NULL,   -- 'seed' | 'user'
                    sort_order     INTEGER NOT NULL DEFAULT 0,
                    alerts_enabled INTEGER NOT NULL DEFAULT 1,
                    notes          TEXT NULL
                );
                -- Single row. seed_hash records the AllowedSymbols the watchlist was seeded from, so
                -- the UI can report that the configured universe has changed since — without ever
                -- re-seeding on its own, which would silently discard the user's edits.
                CREATE TABLE IF NOT EXISTS watchlist_meta (
                    id         INTEGER PRIMARY KEY CHECK (id = 1),
                    seeded_utc TEXT NULL,
                    seed_hash  TEXT NULL
                );
                -- What the monitor remembers between passes. Without it there are no transitions,
                -- only conditions, and a standing situation would be re-reported forever.
                CREATE TABLE IF NOT EXISTS watchlist_state (
                    symbol           TEXT PRIMARY KEY,
                    zone             TEXT NOT NULL,
                    setup            TEXT NOT NULL,
                    support          TEXT NULL,
                    resistance       TEXT NULL,
                    sma_relation     TEXT NULL,
                    rsi_band         TEXT NULL,
                    weekly_breakdown INTEGER NOT NULL DEFAULT 0,
                    streaks_json     TEXT NOT NULL DEFAULT '{}',
                    updated_utc      TEXT NOT NULL
                );
                -- Append-only record of what the monitor raised. evidence_json is snapshotted at raise
                -- time so an alert stays explicable after the market has moved on.
                CREATE TABLE IF NOT EXISTS watchlist_alerts (
                    alert_id        TEXT PRIMARY KEY,
                    symbol          TEXT NOT NULL,
                    kind            TEXT NOT NULL,
                    severity        TEXT NOT NULL,
                    level_price     TEXT NULL,
                    price           TEXT NOT NULL,
                    interval        TEXT NOT NULL,
                    summary         TEXT NOT NULL,
                    evidence_json   TEXT NOT NULL,
                    weekly_confirmed INTEGER NOT NULL DEFAULT 0,
                    from_live_bar   INTEGER NOT NULL DEFAULT 0,
                    state           TEXT NOT NULL DEFAULT 'new',
                    raised_utc      TEXT NOT NULL,
                    session_date    TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_watchlist_alerts_raised
                    ON watchlist_alerts(raised_utc DESC);
                CREATE INDEX IF NOT EXISTS ix_watchlist_alerts_dedupe
                    ON watchlist_alerts(symbol, kind, raised_utc DESC);
                CREATE INDEX IF NOT EXISTS ix_trading_order_events_execution
                    ON trading_order_events(execution_id, event_id);
                CREATE INDEX IF NOT EXISTS ix_intraday_bars_series
                    ON intraday_bars(symbol, interval_minutes, bucket_start_utc DESC);
                CREATE INDEX IF NOT EXISTS ix_daily_bars_series
                    ON daily_bars(symbol, session_date DESC);
                CREATE INDEX IF NOT EXISTS ix_trade_proposals_status_created
                    ON trade_proposals(status, created_utc DESC);
                CREATE INDEX IF NOT EXISTS ix_trading_executions_state_created
                    ON trading_executions(state, created_utc DESC);
                CREATE INDEX IF NOT EXISTS ix_reconciliation_runs_started
                    ON reconciliation_runs(started_utc DESC);
                """;
            await command.ExecuteNonQueryAsync(ct);
            _initialized = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[TradingLedger] Database initialization failed.");
            throw;
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    // ── Watchlist ─────────────────────────────────────────────────────────────

    public async Task<WatchlistSnapshot> GetWatchlistAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);

        var entries = new List<WatchlistEntry>();
        var read = connection.CreateCommand();
        read.CommandText = """
            SELECT symbol, added_utc, source, sort_order, alerts_enabled, notes
            FROM watchlist
            ORDER BY sort_order, symbol
            """;
        await using (var reader = await read.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                entries.Add(new WatchlistEntry(
                    reader.GetString(0),
                    ParseUtc(reader.GetString(1)),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    reader.GetInt64(4) != 0,
                    reader.IsDBNull(5) ? null : reader.GetString(5)));
            }
        }

        var meta = connection.CreateCommand();
        meta.CommandText = "SELECT seeded_utc, seed_hash FROM watchlist_meta WHERE id = 1";
        await using var metaReader = await meta.ExecuteReaderAsync(ct);
        DateTime? seededUtc = null;
        string? seedHash = null;
        if (await metaReader.ReadAsync(ct))
        {
            seededUtc = metaReader.IsDBNull(0) ? null : ParseUtc(metaReader.GetString(0));
            seedHash = metaReader.IsDBNull(1) ? null : metaReader.GetString(1);
        }

        return new WatchlistSnapshot(entries, seededUtc, seedHash);
    }

    public async Task<bool> EnsureWatchlistSeededAsync(
        IReadOnlyList<string> seed, string seedHash, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        // Claim the seeding right by inserting the single meta row. INSERT OR IGNORE means the second
        // caller (another process, or a concurrent first request) inserts nothing and stops here, so
        // the seed cannot be applied twice.
        var claim = connection.CreateCommand();
        claim.Transaction = (SqliteTransaction)transaction;
        claim.CommandText = """
            INSERT OR IGNORE INTO watchlist_meta (id, seeded_utc, seed_hash)
            VALUES (1, $now, $hash)
            """;
        claim.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        claim.Parameters.AddWithValue("$hash", seedHash);
        if (await claim.ExecuteNonQueryAsync(ct) != 1)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }

        await InsertWatchlistSymbolsAsync(connection, (SqliteTransaction)transaction, seed, "seed", ct);
        await transaction.CommitAsync(ct);

        _logger.LogInformation(
            "[Watchlist] Seeded {Count} symbol(s) from AllowedSymbols. Edits from here on are the "
            + "user's; the configured list is no longer followed automatically.", seed.Count);
        return true;
    }

    public async Task<bool> AddWatchlistSymbolAsync(
        string symbol, string source, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);

        // Appended at the end of the display order rather than inserted alphabetically: a symbol the
        // user just added should be where they can see it.
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO watchlist (symbol, added_utc, source, sort_order, alerts_enabled)
            VALUES ($symbol, $now, $source,
                    COALESCE((SELECT MAX(sort_order) + 1 FROM watchlist), 0), 1)
            """;
        command.Parameters.AddWithValue("$symbol", symbol);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$source", source);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> RemoveWatchlistSymbolAsync(string symbol, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM watchlist WHERE symbol = $symbol";
        command.Parameters.AddWithValue("$symbol", symbol);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> UpdateWatchlistSymbolAsync(
        string symbol, bool? alertsEnabled, string? notes, CancellationToken ct = default)
    {
        if (alertsEnabled is null && notes is null) return true;

        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        // COALESCE against the parameter keeps an unspecified field untouched, so a PATCH of one
        // field cannot blank the other.
        command.CommandText = """
            UPDATE watchlist
               SET alerts_enabled = COALESCE($alerts, alerts_enabled),
                   notes          = COALESCE($notes, notes)
             WHERE symbol = $symbol
            """;
        command.Parameters.AddWithValue("$symbol", symbol);
        command.Parameters.AddWithValue("$alerts",
            alertsEnabled is null ? DBNull.Value : alertsEnabled.Value ? 1 : 0);
        command.Parameters.AddWithValue("$notes", notes ?? (object)DBNull.Value);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<int> ResetWatchlistAsync(
        IReadOnlyList<string> seed, string seedHash, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var clear = connection.CreateCommand();
        clear.Transaction = (SqliteTransaction)transaction;
        clear.CommandText = "DELETE FROM watchlist";
        await clear.ExecuteNonQueryAsync(ct);

        await InsertWatchlistSymbolsAsync(connection, (SqliteTransaction)transaction, seed, "seed", ct);

        var stamp = connection.CreateCommand();
        stamp.Transaction = (SqliteTransaction)transaction;
        stamp.CommandText = """
            INSERT INTO watchlist_meta (id, seeded_utc, seed_hash) VALUES (1, $now, $hash)
            ON CONFLICT(id) DO UPDATE SET seeded_utc = $now, seed_hash = $hash
            """;
        stamp.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        stamp.Parameters.AddWithValue("$hash", seedHash);
        await stamp.ExecuteNonQueryAsync(ct);

        await transaction.CommitAsync(ct);
        _logger.LogInformation("[Watchlist] Reset to the configured allowed list ({Count} symbols).",
            seed.Count);
        return seed.Count;
    }

    public async Task<IReadOnlyDictionary<string, int>> GetDailyBarCountsAsync(
        IReadOnlyList<string> symbols, CancellationToken ct = default)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (symbols.Count == 0) return counts;

        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);

        // One grouped scan over the requested symbols rather than a query each: the watchlist can hold
        // a hundred symbols and this feeds a page load.
        var command = connection.CreateCommand();
        var names = new List<string>(symbols.Count);
        for (var i = 0; i < symbols.Count; i++)
        {
            var name = $"$s{i}";
            names.Add(name);
            command.Parameters.AddWithValue(name, symbols[i]);
        }
        command.CommandText =
            $"SELECT symbol, COUNT(*) FROM daily_bars WHERE symbol IN ({string.Join(",", names)}) "
            + "GROUP BY symbol";

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            counts[reader.GetString(0)] = reader.GetInt32(1);
        return counts;
    }

    // ── Monitor state and alerts ──────────────────────────────────────────────

    public async Task<IReadOnlyDictionary<string, SymbolMonitorState>> GetMonitorStatesAsync(
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT symbol, zone, setup, support, resistance, sma_relation, rsi_band,
                   weekly_breakdown, streaks_json, updated_utc
            FROM watchlist_state
            """;

        var states = new Dictionary<string, SymbolMonitorState>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var symbol = reader.GetString(0);
            states[symbol] = new SymbolMonitorState
            {
                Symbol          = symbol,
                Zone            = Enum.TryParse<PriceZone>(reader.GetString(1), out var zone)
                                      ? zone : PriceZone.Unknown,
                Setup           = Enum.TryParse<TradeSetup>(reader.GetString(2), out var setup)
                                      ? setup : TradeSetup.InsufficientData,
                Support         = ParseDecimal(reader, 3),
                Resistance      = ParseDecimal(reader, 4),
                SmaRelation     = reader.IsDBNull(5) ? null : reader.GetString(5),
                RsiBand         = reader.IsDBNull(6) ? null : reader.GetString(6),
                WeeklyBreakdown = reader.GetInt64(7) != 0,
                Streaks         = ParseStreaks(reader.GetString(8)),
                UpdatedUtc      = ParseUtc(reader.GetString(9)),
                IsNew           = false
            };
        }
        return states;
    }

    public async Task SaveMonitorStateAsync(SymbolMonitorState state, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);

        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO watchlist_state
                (symbol, zone, setup, support, resistance, sma_relation, rsi_band,
                 weekly_breakdown, streaks_json, updated_utc)
            VALUES ($symbol, $zone, $setup, $support, $resistance, $sma, $rsi,
                    $breakdown, $streaks, $now)
            ON CONFLICT(symbol) DO UPDATE SET
                zone = $zone, setup = $setup, support = $support, resistance = $resistance,
                sma_relation = $sma, rsi_band = $rsi, weekly_breakdown = $breakdown,
                streaks_json = $streaks, updated_utc = $now
            """;
        command.Parameters.AddWithValue("$symbol", state.Symbol);
        command.Parameters.AddWithValue("$zone", state.Zone.ToString());
        command.Parameters.AddWithValue("$setup", state.Setup.ToString());
        command.Parameters.AddWithValue("$support", Money(state.Support));
        command.Parameters.AddWithValue("$resistance", Money(state.Resistance));
        command.Parameters.AddWithValue("$sma", state.SmaRelation ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$rsi", state.RsiBand ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$breakdown", state.WeeklyBreakdown ? 1 : 0);
        command.Parameters.AddWithValue("$streaks", JsonSerializer.Serialize(
            state.Streaks.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value)));
        command.Parameters.AddWithValue("$now", state.UpdatedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<string> SaveAlertAsync(
        DetectedAlert alert, DateOnly sessionDate, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);

        var id = Guid.NewGuid().ToString("N");
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO watchlist_alerts
                (alert_id, symbol, kind, severity, level_price, price, interval, summary,
                 evidence_json, weekly_confirmed, from_live_bar, state, raised_utc, session_date)
            VALUES ($id, $symbol, $kind, $severity, $level, $price, $interval, $summary,
                    $evidence, $confirmed, $live, 'new', $now, $session)
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$symbol", alert.Symbol);
        command.Parameters.AddWithValue("$kind", alert.Kind.ToString());
        command.Parameters.AddWithValue("$severity", alert.Severity.ToString());
        command.Parameters.AddWithValue("$level", Money(alert.LevelPrice));
        command.Parameters.AddWithValue("$price", alert.Price.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$interval", alert.Interval);
        command.Parameters.AddWithValue("$summary", alert.Summary);
        command.Parameters.AddWithValue("$evidence", JsonSerializer.Serialize(alert.Reasons));
        command.Parameters.AddWithValue("$confirmed", alert.WeeklyConfirmed ? 1 : 0);
        command.Parameters.AddWithValue("$live", alert.FromLiveBar ? 1 : 0);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue(
            "$session", sessionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(ct);
        return id;
    }

    public async Task<bool> HasRecentAlertAsync(
        string symbol, AlertKind kind, decimal? levelPrice, DateTime since, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);

        var command = connection.CreateCommand();
        // Levels are compared as rounded text: the same structural level re-derived on the next pass
        // can differ in the last paisa, and treating that as a new level would defeat the cooldown.
        command.CommandText = """
            SELECT 1 FROM watchlist_alerts
            WHERE symbol = $symbol AND kind = $kind AND raised_utc >= $since
              AND ($level IS NULL OR level_price IS NULL OR level_price = $level)
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$symbol", symbol);
        command.Parameters.AddWithValue("$kind", kind.ToString());
        command.Parameters.AddWithValue("$since", since.ToString("O"));
        command.Parameters.AddWithValue("$level", Money(levelPrice));
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    public async Task<IReadOnlyList<AlertRecord>> GetAlertsAsync(
        string? symbol = null, string? state = null, int limit = 100, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT alert_id, symbol, kind, severity, level_price, price, interval, summary,
                   evidence_json, weekly_confirmed, from_live_bar, state, raised_utc, session_date
            FROM watchlist_alerts
            WHERE ($symbol IS NULL OR symbol = $symbol)
              AND ($state IS NULL OR state = $state)
            ORDER BY raised_utc DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$symbol", (object?)symbol?.ToUpperInvariant() ?? DBNull.Value);
        command.Parameters.AddWithValue("$state", (object?)state ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));

        var alerts = new List<AlertRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            alerts.Add(ReadAlert(reader));
        return alerts;
    }

    /// <summary>Shared projection so the by-id and list queries cannot drift apart.</summary>
    private static AlertRecord ReadAlert(Microsoft.Data.Sqlite.SqliteDataReader reader) => new()
    {
        AlertId         = reader.GetString(0),
        Symbol          = reader.GetString(1),
        Kind            = reader.GetString(2),
        Severity        = reader.GetString(3),
        LevelPrice      = ParseDecimal(reader, 4),
        Price           = decimal.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
        Interval        = reader.GetString(6),
        Summary         = reader.GetString(7),
        Reasons         = JsonSerializer.Deserialize<List<string>>(reader.GetString(8)) ?? [],
        WeeklyConfirmed = reader.GetInt64(9) != 0,
        FromLiveBar     = reader.GetInt64(10) != 0,
        State           = reader.GetString(11),
        RaisedUtc       = ParseUtc(reader.GetString(12)),
        SessionDate     = reader.GetString(13)
    };

    public async Task<AlertRecord?> GetAlertAsync(string alertId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT alert_id, symbol, kind, severity, level_price, price, interval, summary,
                   evidence_json, weekly_confirmed, from_live_bar, state, raised_utc, session_date
            FROM watchlist_alerts
            WHERE alert_id = $id
            """;
        command.Parameters.AddWithValue("$id", alertId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadAlert(reader) : null;
    }

    public async Task<bool> SetAlertStateAsync(string alertId, string state, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE watchlist_alerts SET state = $state WHERE alert_id = $id";
        command.Parameters.AddWithValue("$id", alertId);
        command.Parameters.AddWithValue("$state", state);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<IReadOnlyDictionary<string, int>> GetOpenAlertCountsAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText =
            "SELECT symbol, COUNT(*) FROM watchlist_alerts WHERE state = 'new' GROUP BY symbol";

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            counts[reader.GetString(0)] = reader.GetInt32(1);
        return counts;
    }

    public async Task<int> PruneAlertsAsync(DateTime before, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM watchlist_alerts WHERE raised_utc < $before";
        command.Parameters.AddWithValue("$before", before.ToString("O"));
        return await command.ExecuteNonQueryAsync(ct);
    }

    private static object Money(decimal? value) => value is null
        ? DBNull.Value
        : Math.Round(value.Value, 2, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);

    private static decimal? ParseDecimal(Microsoft.Data.Sqlite.SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : decimal.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture);

    private static IReadOnlyDictionary<AlertKind, int> ParseStreaks(string json)
    {
        var streaks = new Dictionary<AlertKind, int>();
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
            if (raw is null) return streaks;
            foreach (var (key, value) in raw)
            {
                if (Enum.TryParse<AlertKind>(key, out var kind)) streaks[kind] = value;
            }
        }
        catch (JsonException)
        {
            // Corrupt state costs at most one delayed alert; losing the monitor to it would be worse.
        }
        return streaks;
    }

    private static async Task InsertWatchlistSymbolsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<string> symbols,
        string source,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow.ToString("O");
        var order = 0;
        foreach (var symbol in symbols)
        {
            var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO watchlist
                    (symbol, added_utc, source, sort_order, alerts_enabled)
                VALUES ($symbol, $now, $source, $order, 1)
                """;
            insert.Parameters.AddWithValue("$symbol", symbol);
            insert.Parameters.AddWithValue("$now", now);
            insert.Parameters.AddWithValue("$source", source);
            insert.Parameters.AddWithValue("$order", order++);
            await insert.ExecuteNonQueryAsync(ct);
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private static string ResolveWorkspaceRoot(IConfiguration configuration)
    {
        var first = configuration.GetSection("Workspaces").Get<string[]>()
            ?.FirstOrDefault(w => !string.IsNullOrWhiteSpace(w));
        return string.IsNullOrWhiteSpace(first) ? AppContext.BaseDirectory : Path.GetFullPath(first);
    }

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static DateTime ParseUtc(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
