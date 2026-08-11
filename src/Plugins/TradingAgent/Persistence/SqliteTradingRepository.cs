using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.Json;
using TradingAgent.Config;
using TradingAgent.Manager;
using TradingAgent.Reconciliation;

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
