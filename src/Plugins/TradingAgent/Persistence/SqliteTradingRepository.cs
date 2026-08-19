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

    /// <summary>
    /// Upserts one <c>broker_orders</c> row per attempt. Keyed on <c>client_order_id</c>
    /// (<c>{executionId}:{index}</c>) rather than the exchange number, because the exchange number is
    /// exactly what may be missing — and a re-record of the same attempt must update that attempt, not
    /// create a second row for it.
    ///
    /// <para>
    /// An attempt with no exchange number is stored with a <c>pending:</c> broker_order_id. The column is
    /// the table's primary key and cannot be null, and inventing a random id would make the row
    /// indistinguishable from a real order number to every later reader. The prefix says plainly that the
    /// broker never gave us one, and the row is still there to be found.
    /// </para>
    /// </summary>
    public async Task RecordBrokerOrdersAsync(
        string executionId,
        IReadOnlyList<TradingAgent.Models.OrderResult> orders,
        CancellationToken ct = default)
    {
        if (orders.Count == 0) return;

        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var now = DateTime.UtcNow.ToString("O");
        for (var i = 0; i < orders.Count; i++)
        {
            var order = orders[i];
            var clientOrderId = $"{executionId}:{i}";
            var brokerOrderId = string.IsNullOrWhiteSpace(order.OrderId)
                ? $"pending:{clientOrderId}"
                : order.OrderId!.Trim();

            var command = connection.CreateCommand();
            command.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO broker_orders
                    (broker_order_id, execution_id, client_order_id, state, order_json, created_utc, updated_utc)
                VALUES ($brokerId, $executionId, $clientId, $state, $json, $now, $now)
                ON CONFLICT(client_order_id) DO UPDATE SET
                    broker_order_id = excluded.broker_order_id,
                    state           = excluded.state,
                    order_json      = excluded.order_json,
                    updated_utc     = excluded.updated_utc
                """;
            command.Parameters.AddWithValue("$brokerId", brokerOrderId);
            command.Parameters.AddWithValue("$executionId", executionId);
            command.Parameters.AddWithValue("$clientId", clientOrderId);
            command.Parameters.AddWithValue("$state", order.Success ? "accepted" : "failed");
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(order));
            command.Parameters.AddWithValue("$now", now);
            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// Records fills, skipping any already stored. The fill id is derived from the order number, the
    /// timestamp and the quantity, which is what makes a re-read of the same activity log a no-op:
    /// reconciliation sees today's whole log every minute, so an append-only insert would multiply every
    /// fill by the number of passes remaining in the day.
    ///
    /// <para>
    /// A fill whose order this system never placed — a manual order in the portal — is recorded too, and
    /// that needs a parent row because <c>fills.broker_order_id</c> is a foreign key and
    /// <c>PRAGMA foreign_keys</c> is ON. It gets an <c>external:</c> execution id, so a position that
    /// moved for reasons outside this system is visible rather than silently dropped by a constraint.
    /// </para>
    /// </summary>
    public async Task<int> RecordFillsAsync(
        IReadOnlyList<TradingAgent.Reconciliation.BrokerFill> fills,
        CancellationToken ct = default)
    {
        if (fills.Count == 0) return 0;

        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var inserted = 0;
        var now = DateTime.UtcNow.ToString("O");

        foreach (var fill in fills)
        {
            var orderNo = fill.OrderNo.Trim();
            if (orderNo.Length == 0) continue;

            // Parent row for an order we did not place, so the foreign key holds. INSERT OR IGNORE, so an
            // order this system DID place keeps the row (and the state) it already has.
            var parent = connection.CreateCommand();
            parent.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
            parent.CommandText = """
                INSERT OR IGNORE INTO trading_executions
                    (execution_id, idempotency_key, state, request_json, policy_version, created_utc, updated_utc)
                VALUES ($externalId, $externalId, 'external', '{}', 'external', $now, $now);

                INSERT OR IGNORE INTO broker_orders
                    (broker_order_id, execution_id, client_order_id, state, order_json, created_utc, updated_utc)
                VALUES ($orderNo, $externalId, $clientId, 'external', $json, $now, $now);
                """;
            parent.Parameters.AddWithValue("$externalId", $"external:{orderNo}");
            parent.Parameters.AddWithValue("$orderNo", orderNo);
            parent.Parameters.AddWithValue("$clientId", $"external:{orderNo}");
            parent.Parameters.AddWithValue("$json", JsonSerializer.Serialize(fill));
            parent.Parameters.AddWithValue("$now", now);
            await parent.ExecuteNonQueryAsync(ct);

            var command = connection.CreateCommand();
            command.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO fills (fill_id, broker_order_id, quantity, price, filled_utc)
                VALUES ($fillId, $orderNo, $quantity, $price, $filledUtc)
                """;
            command.Parameters.AddWithValue("$fillId",
                $"{orderNo}:{fill.FilledUtc:yyyyMMddTHHmmss}:{fill.Quantity}");
            command.Parameters.AddWithValue("$orderNo", orderNo);
            command.Parameters.AddWithValue("$quantity", fill.Quantity);
            command.Parameters.AddWithValue("$price", fill.Price.ToString(CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$filledUtc", fill.FilledUtc.ToString("O"));
            inserted += await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return inserted;
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
        IReadOnlyCollection<string> requestedSymbols,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var session = sessionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var nowUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        // Symbols whose bar was rejected as unsettled must not be claimed as covered: recording them
        // would freeze the gap the guard below exists to avoid.
        var unsettled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stored = 0;

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
                if (bar.IsLive || bar.IsIntraday)
                {
                    unsettled.Add(bar.Symbol);
                    continue;
                }

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
                stored++;
            }
        }

        var coverage = connection.CreateCommand();
        coverage.Transaction = (SqliteTransaction)transaction;
        // market_closed is left alone rather than forced to 0: a date already known to be a non-trading
        // day stays covered for every symbol, and re-saving it must not narrow that.
        coverage.CommandText = """
            INSERT INTO daily_bar_coverage (session_date, symbol_count, fetched_utc, market_closed)
            VALUES ($session, $count, $fetched, 0)
            ON CONFLICT (session_date) DO UPDATE SET
                symbol_count = excluded.symbol_count, fetched_utc = excluded.fetched_utc
            """;
        coverage.Parameters.AddWithValue("$session", session);
        coverage.Parameters.AddWithValue("$count", stored);
        coverage.Parameters.AddWithValue("$fetched", nowUtc);
        await coverage.ExecuteNonQueryAsync(ct);

        if (requestedSymbols.Count > 0)
        {
            var mark = connection.CreateCommand();
            mark.Transaction = (SqliteTransaction)transaction;
            mark.CommandText = """
                INSERT OR IGNORE INTO daily_bar_coverage_symbols (session_date, symbol)
                VALUES ($session, $symbol)
                """;
            mark.Parameters.AddWithValue("$session", session);
            var covered = mark.Parameters.Add("$symbol", SqliteType.Text);

            foreach (var requested in Normalize(requestedSymbols))
            {
                if (unsettled.Contains(requested)) continue;
                covered.Value = requested;
                await mark.ExecuteNonQueryAsync(ct);
            }
        }

        await transaction.CommitAsync(ct);
    }

    public async Task SaveNonTradingDayAsync(DateOnly sessionDate, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var session = sessionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var coverage = connection.CreateCommand();
        coverage.Transaction = (SqliteTransaction)transaction;
        coverage.CommandText = """
            INSERT INTO daily_bar_coverage (session_date, symbol_count, fetched_utc, market_closed)
            VALUES ($session, 0, $fetched, 1)
            ON CONFLICT (session_date) DO UPDATE SET
                symbol_count = 0, fetched_utc = excluded.fetched_utc, market_closed = 1
            """;
        coverage.Parameters.AddWithValue("$session", session);
        coverage.Parameters.AddWithValue("$fetched", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await coverage.ExecuteNonQueryAsync(ct);

        // Per-symbol rows are redundant once the whole market is known to have been shut, and leaving
        // them would let the two records disagree about who is covered.
        var drop = connection.CreateCommand();
        drop.Transaction = (SqliteTransaction)transaction;
        drop.CommandText = "DELETE FROM daily_bar_coverage_symbols WHERE session_date = $session";
        drop.Parameters.AddWithValue("$session", session);
        await drop.ExecuteNonQueryAsync(ct);

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
        IReadOnlyCollection<string> symbols,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);

        var wanted = Normalize(symbols);
        var command = connection.CreateCommand();

        if (wanted.Count == 0)
        {
            // No universe to check against, so "on record at all" is the only answer available.
            command.CommandText = """
                SELECT session_date FROM daily_bar_coverage
                WHERE session_date >= $from AND session_date <= $to
                """;
        }
        else
        {
            // A date is skippable only when every requested symbol has been asked for on it. Counting
            // matched rows against the requested total is exact because (session_date, symbol) is the
            // primary key, so the count can never exceed it.
            var names = new List<string>(wanted.Count);
            for (var i = 0; i < wanted.Count; i++)
            {
                var name = $"$s{i}";
                names.Add(name);
                command.Parameters.AddWithValue(name, wanted[i]);
            }

            command.CommandText = $"""
                SELECT c.session_date
                FROM daily_bar_coverage c
                WHERE c.session_date >= $from AND c.session_date <= $to
                  AND (c.market_closed = 1
                       OR (SELECT COUNT(*)
                           FROM daily_bar_coverage_symbols s
                           WHERE s.session_date = c.session_date
                             AND s.symbol IN ({string.Join(",", names)})) = $wanted)
                """;
            command.Parameters.AddWithValue("$wanted", wanted.Count);
        }

        command.Parameters.AddWithValue("$from", fromInclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$to", toInclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        var dates = new HashSet<DateOnly>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            dates.Add(DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture));

        return dates;
    }

    public async Task<IReadOnlyDictionary<string, int>> GetCoveredDailyDateCountsAsync(
        DateOnly fromInclusive,
        DateOnly toInclusive,
        IReadOnlyCollection<string> symbols,
        CancellationToken ct = default)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var wanted = Normalize(symbols);
        if (wanted.Count == 0) return counts;

        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);

        var from = fromInclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var to = toInclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // Non-trading days are covered for every symbol, so they are counted once here rather than
        // stored as a row per symbol.
        var closedCommand = connection.CreateCommand();
        closedCommand.CommandText = """
            SELECT COUNT(*) FROM daily_bar_coverage
            WHERE session_date >= $from AND session_date <= $to AND market_closed = 1
            """;
        closedCommand.Parameters.AddWithValue("$from", from);
        closedCommand.Parameters.AddWithValue("$to", to);
        var closed = Convert.ToInt32(await closedCommand.ExecuteScalarAsync(ct) ?? 0);

        var command = connection.CreateCommand();
        var names = new List<string>(wanted.Count);
        for (var i = 0; i < wanted.Count; i++)
        {
            var name = $"$s{i}";
            names.Add(name);
            command.Parameters.AddWithValue(name, wanted[i]);
        }

        // Joined so a per-symbol row left over from before a date was reclassified as non-trading is not
        // counted a second time on top of the market-wide total.
        command.CommandText = $"""
            SELECT s.symbol, COUNT(*)
            FROM daily_bar_coverage_symbols s
            JOIN daily_bar_coverage c ON c.session_date = s.session_date
            WHERE s.session_date >= $from AND s.session_date <= $to
              AND c.market_closed = 0
              AND s.symbol IN ({string.Join(",", names)})
            GROUP BY s.symbol
            """;
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            counts[reader.GetString(0)] = reader.GetInt32(1) + closed;

        if (closed > 0)
            foreach (var symbol in wanted)
                if (!counts.ContainsKey(symbol)) counts[symbol] = closed;

        return counts;
    }

    public async Task<int> ClearDailyCoverageAfterAsync(
        DateOnly settledThrough, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var through = settledThrough.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "DELETE FROM daily_bar_coverage WHERE session_date > $through";
        command.Parameters.AddWithValue("$through", through);
        var removed = await command.ExecuteNonQueryAsync(ct);

        // Dropped with the date marker: a per-symbol row surviving on its own would leave the date
        // refetchable yet still counted as covered for the symbols recorded prematurely.
        var symbolRows = connection.CreateCommand();
        symbolRows.Transaction = (SqliteTransaction)transaction;
        symbolRows.CommandText =
            "DELETE FROM daily_bar_coverage_symbols WHERE session_date > $through";
        symbolRows.Parameters.AddWithValue("$through", through);
        await symbolRows.ExecuteNonQueryAsync(ct);

        await transaction.CommitAsync(ct);

        if (removed > 0)
            _logger.LogInformation(
                "[TradingLedger] Cleared {Count} unsettled daily-coverage marker(s) after {Through}; "
                + "those sessions will be fetched again once settled.", removed, settledThrough);
        return removed;
    }

    /// <summary>Trimmed, upper-cased, de-duplicated symbols, matching how they are stored.</summary>
    private static List<string> Normalize(IReadOnlyCollection<string> symbols) =>
        [.. symbols
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)];

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
                CREATE INDEX IF NOT EXISTS ix_broker_orders_execution
                    ON broker_orders(execution_id);
                CREATE INDEX IF NOT EXISTS ix_fills_order
                    ON fills(broker_order_id);
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
                -- Which dates have been retrieved at all, so the backfill is resumable. market_closed
                -- marks a day the market did not trade, which is covered for every symbol forever;
                -- symbol_count is informational (how many bars that fetch stored).
                CREATE TABLE IF NOT EXISTS daily_bar_coverage (
                    session_date TEXT PRIMARY KEY,
                    symbol_count INTEGER NOT NULL,
                    fetched_utc TEXT NOT NULL,
                    market_closed INTEGER NOT NULL DEFAULT 0
                );
                -- Which symbols each trading date was actually requested for. A session fetch returns
                -- the whole market and is then filtered to the archive universe, so a date-only marker
                -- lost the one fact that matters here: WHICH symbols it covered. Without it, a symbol
                -- added to the universe after a date was fetched could never be filled in — the date
                -- counted as covered, the backfill skipped it, and the symbol stayed permanently short
                -- of the history weekly levels need. Absence of a row means "never requested", not
                -- "did not trade", which is what makes a symbol-targeted backfill possible.
                CREATE TABLE IF NOT EXISTS daily_bar_coverage_symbols (
                    session_date TEXT NOT NULL,
                    symbol       TEXT NOT NULL,
                    PRIMARY KEY (session_date, symbol)
                ) WITHOUT ROWID;
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
                    pinned         INTEGER NOT NULL DEFAULT 0,
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
                -- Orders waiting on a condition. Durable so an armed trigger survives a restart, which
                -- is the whole point: a stop that forgets itself when the process bounces is not a stop.
                CREATE TABLE IF NOT EXISTS armed_orders (
                    armed_id      TEXT PRIMARY KEY,
                    symbol        TEXT NOT NULL,
                    trigger_kind  TEXT NOT NULL,
                    trigger_price TEXT NULL,
                    trigger_alert TEXT NULL,
                    action        TEXT NOT NULL,
                    quantity      INTEGER NOT NULL,
                    order_type    TEXT NOT NULL,
                    price         TEXT NULL,
                    limit_price   TEXT NULL,
                    state         TEXT NOT NULL DEFAULT 'armed',
                    armed_utc     TEXT NOT NULL,
                    expires_utc   TEXT NULL,
                    fired_utc     TEXT NULL,
                    execution_id  TEXT NULL,
                    state_reason  TEXT NULL,
                    note          TEXT NULL,
                    source_alert  TEXT NULL,
                    protective_stop_id TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_armed_orders_state
                    ON armed_orders(state, symbol);
                -- A standing intent to keep a position protected, NOT a queued order. The venue clears
                -- outstanding orders at the close, so the durable thing has to be the intent and the
                -- native day order is re-derived from it each session.
                CREATE TABLE IF NOT EXISTS protective_stops (
                    stop_id           TEXT PRIMARY KEY,
                    symbol            TEXT NOT NULL,
                    parent_armed_id   TEXT NULL,
                    stop_trigger      TEXT NOT NULL,
                    stop_limit        TEXT NOT NULL,
                    desired_qty       INTEGER NOT NULL DEFAULT 0,
                    recurring         INTEGER NOT NULL DEFAULT 1,
                    state             TEXT NOT NULL DEFAULT 'pending_fill',
                    -- NULL means "never captured", which is not zero: a stop with no baseline cannot
                    -- measure a fill and refuses to activate rather than sizing a sell off a guess.
                    baseline_qty      INTEGER NULL,
                    placed_qty        INTEGER NOT NULL DEFAULT 0,
                    last_placed_date  TEXT NULL,
                    last_order_no     TEXT NULL,
                    backstop_armed_id TEXT NULL,
                    created_utc       TEXT NOT NULL,
                    fill_confirmed_utc TEXT NULL,
                    closed_utc        TEXT NULL,
                    state_reason      TEXT NULL,
                    note              TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_protective_stops_state
                    ON protective_stops(state, symbol);
                CREATE INDEX IF NOT EXISTS ix_protective_stops_parent
                    ON protective_stops(parent_armed_id);
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
                -- The primary key already orders by date; this serves the other direction, counting one
                -- symbol's covered dates across a range.
                CREATE INDEX IF NOT EXISTS ix_daily_bar_coverage_symbols_symbol
                    ON daily_bar_coverage_symbols(symbol, session_date);
                CREATE INDEX IF NOT EXISTS ix_trade_proposals_status_created
                    ON trade_proposals(status, created_utc DESC);
                CREATE INDEX IF NOT EXISTS ix_trading_executions_state_created
                    ON trading_executions(state, created_utc DESC);
                CREATE INDEX IF NOT EXISTS ix_reconciliation_runs_started
                    ON reconciliation_runs(started_utc DESC);
                """;
            await command.ExecuteNonQueryAsync(ct);

            // ── Additive migrations ───────────────────────────────────────────
            // CREATE TABLE IF NOT EXISTS never alters an existing table, so columns added after a
            // database was first created have to be applied separately. Each is attempted
            // independently and a "duplicate column" failure is the expected no-op on an
            // already-migrated database — cheaper and clearer than maintaining a version table for
            // what are purely additive, nullable columns.
            await AddColumnIfMissingAsync(connection, "trade_proposals", "execution_id", "TEXT NULL", ct);
            await AddColumnIfMissingAsync(connection, "trade_proposals", "state_reason", "TEXT NULL", ct);
            await AddColumnIfMissingAsync(connection, "trade_proposals", "terminal_utc", "TEXT NULL", ct);
            await AddColumnIfMissingAsync(
                connection, "daily_bar_coverage", "market_closed", "INTEGER NOT NULL DEFAULT 0", ct);
            await AddColumnIfMissingAsync(
                connection, "armed_orders", "protective_stop_id", "TEXT NULL", ct);
            await AddColumnIfMissingAsync(
                connection, "watchlist", "pinned", "INTEGER NOT NULL DEFAULT 0", ct);
            await SeedPerSymbolCoverageAsync(connection, ct);

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
            SELECT symbol, added_utc, source, sort_order, pinned, alerts_enabled, notes
            FROM watchlist
            ORDER BY pinned DESC, sort_order, symbol
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
                    reader.GetInt64(5) != 0,
                    reader.IsDBNull(6) ? null : reader.GetString(6)));
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
        string symbol, bool? alertsEnabled, string? notes, bool? pinned = null,
        CancellationToken ct = default)
    {
        if (alertsEnabled is null && notes is null && pinned is null) return true;

        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        // COALESCE against the parameter keeps an unspecified field untouched, so a PATCH of one
        // field cannot blank the other.
        command.CommandText = """
            UPDATE watchlist
               SET alerts_enabled = COALESCE($alerts, alerts_enabled),
                   notes          = COALESCE($notes, notes),
                   pinned         = COALESCE($pinned, pinned)
             WHERE symbol = $symbol
            """;
        command.Parameters.AddWithValue("$symbol", symbol);
        command.Parameters.AddWithValue("$alerts",
            alertsEnabled is null ? DBNull.Value : alertsEnabled.Value ? 1 : 0);
        command.Parameters.AddWithValue("$notes", notes ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$pinned",
            pinned is null ? DBNull.Value : pinned.Value ? 1 : 0);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> ReorderWatchlistAsync(
        IReadOnlyList<string> symbols, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var read = connection.CreateCommand();
        read.Transaction = (SqliteTransaction)transaction;
        read.CommandText = "SELECT symbol FROM watchlist";
        await using (var reader = await read.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) existing.Add(reader.GetString(0));

        var requested = symbols
            .Select(s => s.Trim().ToUpperInvariant())
            .Where(s => s.Length > 0)
            .ToList();
        if (requested.Count != existing.Count
            || requested.Distinct(StringComparer.OrdinalIgnoreCase).Count() != requested.Count
            || requested.Any(s => !existing.Contains(s)))
        {
            await transaction.RollbackAsync(ct);
            return false;
        }

        for (var i = 0; i < requested.Count; i++)
        {
            var update = connection.CreateCommand();
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText = "UPDATE watchlist SET sort_order = $order WHERE symbol = $symbol";
            update.Parameters.AddWithValue("$order", i);
            update.Parameters.AddWithValue("$symbol", requested[i]);
            await update.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return true;
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

    // ── Armed orders ──────────────────────────────────────────────────────────

    public async Task<string> SaveArmedOrderAsync(ArmedOrder order, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO armed_orders
                (armed_id, symbol, trigger_kind, trigger_price, trigger_alert, action, quantity,
                 order_type, price, limit_price, state, armed_utc, expires_utc, note, source_alert,
                 protective_stop_id)
            VALUES ($id, $symbol, $kind, $tprice, $talert, $action, $qty,
                    $otype, $price, $limit, $state, $armed, $expires, $note, $alert, $stop)
            """;
        command.Parameters.AddWithValue("$id", order.ArmedId);
        command.Parameters.AddWithValue("$symbol", order.Symbol);
        command.Parameters.AddWithValue("$kind", order.TriggerKind.ToString());
        command.Parameters.AddWithValue("$tprice", Money(order.TriggerPrice));
        command.Parameters.AddWithValue("$talert",
            order.TriggerAlertKind?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$action", order.Action);
        command.Parameters.AddWithValue("$qty", order.Quantity);
        command.Parameters.AddWithValue("$otype", order.OrderType);
        command.Parameters.AddWithValue("$price", Money(order.Price));
        command.Parameters.AddWithValue("$limit", Money(order.LimitPrice));
        command.Parameters.AddWithValue("$state", order.State);
        command.Parameters.AddWithValue("$armed", order.ArmedUtc.ToString("O"));
        command.Parameters.AddWithValue("$expires",
            order.ExpiresUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$note", order.Note ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$alert", order.SourceAlertId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$stop", order.ProtectiveStopId ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
        return order.ArmedId;
    }

    public async Task<IReadOnlyList<ArmedOrder>> GetArmedOrdersAsync(
        bool armedOnly = true, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT armed_id, symbol, trigger_kind, trigger_price, trigger_alert, action, quantity,
                   order_type, price, limit_price, state, armed_utc, expires_utc, fired_utc,
                   execution_id, state_reason, note, source_alert, protective_stop_id
            FROM armed_orders
            {(armedOnly ? "WHERE state = 'armed'" : "")}
            ORDER BY armed_utc DESC
            """;

        var orders = new List<ArmedOrder>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) orders.Add(ReadArmedOrder(reader));
        return orders;
    }

    public async Task<bool> TrySetArmedOrderStateAsync(
        string armedId,
        string expectedState,
        string newState,
        string? reason = null,
        string? executionId = null,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);

        // Compare-and-set, so a trigger evaluated by two overlapping passes cannot fire twice.
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE armed_orders
               SET state = $new,
                   state_reason = COALESCE($reason, state_reason),
                   execution_id = COALESCE($execution, execution_id),
                   fired_utc = CASE WHEN $new = 'fired' THEN $now ELSE fired_utc END
             WHERE armed_id = $id AND state = $expected
            """;
        command.Parameters.AddWithValue("$id", armedId);
        command.Parameters.AddWithValue("$expected", expectedState);
        command.Parameters.AddWithValue("$new", newState);
        command.Parameters.AddWithValue("$reason", reason ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$execution", executionId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    private static ArmedOrder ReadArmedOrder(Microsoft.Data.Sqlite.SqliteDataReader reader) => new()
    {
        ArmedId          = reader.GetString(0),
        Symbol           = reader.GetString(1),
        TriggerKind      = Enum.TryParse<ArmedTriggerKind>(reader.GetString(2), out var k)
                               ? k : ArmedTriggerKind.PriceBelow,
        TriggerPrice     = ParseDecimal(reader, 3),
        TriggerAlertKind = reader.IsDBNull(4)
                               ? null
                               : Enum.TryParse<AlertKind>(reader.GetString(4), out var a) ? a : null,
        Action           = reader.GetString(5),
        Quantity         = reader.GetInt32(6),
        OrderType        = reader.GetString(7),
        Price            = ParseDecimal(reader, 8),
        LimitPrice       = ParseDecimal(reader, 9),
        State            = reader.GetString(10),
        ArmedUtc         = ParseUtc(reader.GetString(11)),
        ExpiresUtc       = reader.IsDBNull(12) ? null : ParseUtc(reader.GetString(12)),
        FiredUtc         = reader.IsDBNull(13) ? null : ParseUtc(reader.GetString(13)),
        ExecutionId      = reader.IsDBNull(14) ? null : reader.GetString(14),
        StateReason      = reader.IsDBNull(15) ? null : reader.GetString(15),
        Note             = reader.IsDBNull(16) ? null : reader.GetString(16),
        SourceAlertId    = reader.IsDBNull(17) ? null : reader.GetString(17),
        ProtectiveStopId = reader.IsDBNull(18) ? null : reader.GetString(18)
    };

    // ── Protective stops ──────────────────────────────────────────────────────

    public async Task<string> SaveProtectiveStopAsync(
        ProtectiveStop stop, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO protective_stops
                (stop_id, symbol, parent_armed_id, stop_trigger, stop_limit, desired_qty, recurring,
                 state, baseline_qty, placed_qty, backstop_armed_id, created_utc, state_reason, note)
            VALUES ($id, $symbol, $parent, $trigger, $limit, $desired, $recurring,
                    $state, $baseline, $placed, $backstop, $created, $reason, $note)
            """;
        command.Parameters.AddWithValue("$id", stop.StopId);
        command.Parameters.AddWithValue("$symbol", stop.Symbol);
        command.Parameters.AddWithValue("$parent", stop.ParentArmedId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$trigger", Money(stop.StopTrigger));
        command.Parameters.AddWithValue("$limit", Money(stop.StopLimit));
        command.Parameters.AddWithValue("$desired", stop.DesiredQuantity);
        command.Parameters.AddWithValue("$recurring", stop.Recurring ? 1 : 0);
        command.Parameters.AddWithValue("$state", stop.State);
        command.Parameters.AddWithValue("$baseline",
            stop.BaselineQuantity is { } baseline ? baseline : (object)DBNull.Value);
        command.Parameters.AddWithValue("$placed", stop.PlacedQuantity);
        command.Parameters.AddWithValue("$backstop", stop.LocalBackstopArmedId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$created", stop.CreatedUtc.ToString("O"));
        command.Parameters.AddWithValue("$reason", stop.StateReason ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$note", stop.Note ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
        return stop.StopId;
    }

    public async Task<IReadOnlyList<ProtectiveStop>> GetProtectiveStopsAsync(
        bool openOnly = true, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT stop_id, symbol, parent_armed_id, stop_trigger, stop_limit, desired_qty, recurring,
                   state, baseline_qty, placed_qty, last_placed_date, last_order_no, backstop_armed_id,
                   created_utc, fill_confirmed_utc, closed_utc, state_reason, note
            FROM protective_stops
            {(openOnly ? "WHERE state <> 'closed'" : "")}
            ORDER BY created_utc DESC
            """;

        var stops = new List<ProtectiveStop>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) stops.Add(ReadProtectiveStop(reader));
        return stops;
    }

    public async Task<bool> TrySetProtectiveStopStateAsync(
        string stopId,
        string expectedState,
        string newState,
        string? reason = null,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);

        // Compare-and-set for the same reason the armed orders use one: two passes overlapping must
        // not both conclude they are the one promoting this stop.
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE protective_stops
               SET state = $new,
                   state_reason = COALESCE($reason, state_reason),
                   closed_utc = CASE WHEN $new = 'closed' THEN $now ELSE closed_utc END
             WHERE stop_id = $id AND state = $expected
            """;
        command.Parameters.AddWithValue("$id", stopId);
        command.Parameters.AddWithValue("$expected", expectedState);
        command.Parameters.AddWithValue("$new", newState);
        command.Parameters.AddWithValue("$reason", reason ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>
    /// Promotes a stop on confirmed shares. The quantity is RAISED, never lowered: a later pass
    /// seeing a smaller delta must not shrink protection that a bigger fill already established.
    /// </summary>
    public async Task<bool> RecordProtectiveStopFillAsync(
        string stopId, int confirmedQuantity, string reason, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE protective_stops
               SET state = 'active',
                   desired_qty = MAX(desired_qty, $qty),
                   state_reason = $reason,
                   fill_confirmed_utc = COALESCE(fill_confirmed_utc, $now)
             WHERE stop_id = $id AND state IN ('pending_fill', 'active')
            """;
        command.Parameters.AddWithValue("$id", stopId);
        command.Parameters.AddWithValue("$qty", confirmedQuantity);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>
    /// Records a native placement. Coverage ACCUMULATES within a session and resets when the session
    /// rolls, mirroring the venue: yesterday's resting order was cleared at the close and protects
    /// nothing today, so carrying its quantity forward would report protection that does not exist.
    /// </summary>
    public async Task<bool> RecordProtectiveStopPlacementAsync(
        string stopId,
        DateOnly sessionDate,
        int placedQuantity,
        string? orderNo,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        var date = sessionDate.ToString("yyyy-MM-dd");
        command.CommandText = """
            UPDATE protective_stops
               SET placed_qty = CASE WHEN last_placed_date = $date
                                     THEN placed_qty + $qty
                                     ELSE $qty END,
                   last_placed_date = $date,
                   last_order_no = COALESCE($orderNo, last_order_no)
             WHERE stop_id = $id AND state = 'active'
            """;
        command.Parameters.AddWithValue("$id", stopId);
        command.Parameters.AddWithValue("$date", date);
        command.Parameters.AddWithValue("$qty", placedQuantity);
        command.Parameters.AddWithValue("$orderNo", orderNo ?? (object)DBNull.Value);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>
    /// Refreshes the pre-entry holding a fill will later be measured against. Only meaningful while
    /// the entry has not gone in yet, which is why it is confined to <c>pending_fill</c>: overwriting
    /// it afterwards would erase the very number that proves a fill happened.
    /// </summary>
    public async Task<bool> RecordProtectiveStopBaselineAsync(
        string stopId, int baselineQuantity, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE protective_stops
               SET baseline_qty = $qty
             WHERE stop_id = $id AND state = 'pending_fill'
            """;
        command.Parameters.AddWithValue("$id", stopId);
        command.Parameters.AddWithValue("$qty", baselineQuantity);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> SetProtectiveStopBackstopAsync(
        string stopId, string? backstopArmedId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE protective_stops SET backstop_armed_id = $backstop WHERE stop_id = $id";
        command.Parameters.AddWithValue("$id", stopId);
        command.Parameters.AddWithValue("$backstop", backstopArmedId ?? (object)DBNull.Value);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    private static ProtectiveStop ReadProtectiveStop(Microsoft.Data.Sqlite.SqliteDataReader reader) => new()
    {
        StopId               = reader.GetString(0),
        Symbol               = reader.GetString(1),
        ParentArmedId        = reader.IsDBNull(2) ? null : reader.GetString(2),
        StopTrigger          = ParseDecimal(reader, 3) ?? 0m,
        StopLimit            = ParseDecimal(reader, 4) ?? 0m,
        DesiredQuantity      = reader.GetInt32(5),
        Recurring            = reader.GetInt32(6) != 0,
        State                = reader.GetString(7),
        BaselineQuantity     = reader.IsDBNull(8) ? null : reader.GetInt32(8),
        PlacedQuantity       = reader.GetInt32(9),
        LastPlacedSessionDate = reader.IsDBNull(10)
                                   ? null
                                   : DateOnly.ParseExact(reader.GetString(10), "yyyy-MM-dd", CultureInfo.InvariantCulture),
        LastOrderNo          = reader.IsDBNull(11) ? null : reader.GetString(11),
        LocalBackstopArmedId = reader.IsDBNull(12) ? null : reader.GetString(12),
        CreatedUtc           = ParseUtc(reader.GetString(13)),
        FillConfirmedUtc     = reader.IsDBNull(14) ? null : ParseUtc(reader.GetString(14)),
        ClosedUtc            = reader.IsDBNull(15) ? null : ParseUtc(reader.GetString(15)),
        StateReason          = reader.IsDBNull(16) ? null : reader.GetString(16),
        Note                 = reader.IsDBNull(17) ? null : reader.GetString(17)
    };

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

    /// <summary>
    /// Adds a column when it is absent, using the table's own metadata rather than catching an error —
    /// so a genuine failure (locked database, bad type) still surfaces instead of being swallowed as
    /// "already migrated".
    /// </summary>
    private static async Task AddColumnIfMissingAsync(
        SqliteConnection connection, string table, string column, string definition, CancellationToken ct)
    {
        var probe = connection.CreateCommand();
        probe.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = $name";
        probe.Parameters.AddWithValue("$name", column);
        if (await probe.ExecuteScalarAsync(ct) is not null) return;

        var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        await alter.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Reconstructs per-symbol coverage for a database written before coverage was symbol-aware.
    ///
    /// <para>
    /// The old schema recorded only that a date had been fetched, not which symbols it was filtered to.
    /// Read literally after the upgrade, every date would look unrequested for every symbol and the
    /// first pass would ask the portal for two years of history the archive already holds. The bars
    /// themselves carry the missing fact: a symbol with a bar on a date was plainly requested for it.
    /// A date recorded with no bars at all was a market-wide non-trading day, so it stays covered for
    /// everyone via <c>market_closed</c> rather than needing a row per symbol.
    /// </para>
    ///
    /// <para>
    /// Runs once — the pair table being non-empty means an earlier start already did this. The one
    /// inaccuracy is a trading date on which an established symbol genuinely did not trade: it has no
    /// bar to derive from, so it reads as unrequested and gets refetched once, after which the real
    /// coverage row is written. That self-heals in a single request and cannot lose data.
    /// </para>
    /// </summary>
    private async Task SeedPerSymbolCoverageAsync(SqliteConnection connection, CancellationToken ct)
    {
        var probe = connection.CreateCommand();
        probe.CommandText = """
            SELECT (SELECT COUNT(*) FROM daily_bar_coverage_symbols),
                   (SELECT COUNT(*) FROM daily_bar_coverage)
            """;
        await using (var reader = await probe.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return;
            // Already migrated, or a fresh database with nothing to reconstruct.
            if (reader.GetInt64(0) > 0 || reader.GetInt64(1) == 0) return;
        }

        await using var transaction = await connection.BeginTransactionAsync(ct);

        var closed = connection.CreateCommand();
        closed.Transaction = (SqliteTransaction)transaction;
        closed.CommandText = "UPDATE daily_bar_coverage SET market_closed = 1 WHERE symbol_count = 0";
        var closedDays = await closed.ExecuteNonQueryAsync(ct);

        var pairs = connection.CreateCommand();
        pairs.Transaction = (SqliteTransaction)transaction;
        // Joined to coverage so a bar written for a date that was never registered as fetched does not
        // invent coverage for it.
        pairs.CommandText = """
            INSERT OR IGNORE INTO daily_bar_coverage_symbols (session_date, symbol)
            SELECT b.session_date, b.symbol
            FROM daily_bars b
            JOIN daily_bar_coverage c ON c.session_date = b.session_date
            WHERE c.market_closed = 0
            """;
        var seeded = await pairs.ExecuteNonQueryAsync(ct);

        await transaction.CommitAsync(ct);
        _logger.LogInformation(
            "[TradingLedger] Migrated daily coverage to per-symbol: {Pairs} (date, symbol) pairs "
            + "derived from archived bars, {Closed} non-trading day(s) marked covered for all symbols.",
            seeded, closedDays);
    }

    // ── Proposal lifecycle ────────────────────────────────────────────────────

    public async Task<TradeProposalRecord?> GetProposalAsync(
        string proposalId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT proposal_id, status, proposal_json, policy_version, created_utc, updated_utc,
                   execution_id, state_reason
            FROM trade_proposals WHERE proposal_id = $id
            """;
        command.Parameters.AddWithValue("$id", proposalId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadProposal(reader) : null;
    }

    public async Task<bool> TrySetProposalStateAsync(
        string proposalId,
        string expectedStatus,
        string newStatus,
        string? reason = null,
        string? executionId = null,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);

        // Compare-and-set on the CURRENT status. This is what makes a double-click safe: the second
        // request finds the row already moved on and returns false rather than executing the same
        // proposal twice. Same discipline as the execution idempotency claim.
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE trade_proposals
               SET status = $new,
                   state_reason = COALESCE($reason, state_reason),
                   execution_id = COALESCE($execution, execution_id),
                   updated_utc = $now,
                   terminal_utc = CASE WHEN $new IN ('executed','rejected','expired')
                                       THEN $now ELSE terminal_utc END
             WHERE proposal_id = $id AND status = $expected
            """;
        command.Parameters.AddWithValue("$id", proposalId);
        command.Parameters.AddWithValue("$expected", expectedStatus);
        command.Parameters.AddWithValue("$new", newStatus);
        command.Parameters.AddWithValue("$reason", reason ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$execution", executionId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));

        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<IReadOnlyList<TradeProposalRecord>> GetOpenProposalsAsync(
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT proposal_id, status, proposal_json, policy_version, created_utc, updated_utc,
                   execution_id, state_reason
            FROM trade_proposals
            WHERE status NOT IN ('executed','rejected','expired')
            ORDER BY created_utc DESC
            """;

        var proposals = new List<TradeProposalRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) proposals.Add(ReadProposal(reader));
        return proposals;
    }

    public async Task<int> PruneProposalsAsync(DateTime before, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        // Only TERMINAL rows are pruned; an open proposal is never silently discarded by retention.
        command.CommandText = """
            DELETE FROM trade_proposals
            WHERE status IN ('executed','rejected','expired')
              AND COALESCE(terminal_utc, updated_utc) < $before
            """;
        command.Parameters.AddWithValue("$before", before.ToString("O"));
        return await command.ExecuteNonQueryAsync(ct);
    }

    private static TradeProposalRecord ReadProposal(Microsoft.Data.Sqlite.SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        ParseJson(reader.GetString(2)),
        reader.GetString(3),
        ParseUtc(reader.GetString(4)),
        ParseUtc(reader.GetString(5)))
    {
        ExecutionId = reader.FieldCount > 6 && !reader.IsDBNull(6) ? reader.GetString(6) : null,
        StateReason = reader.FieldCount > 7 && !reader.IsDBNull(7) ? reader.GetString(7) : null
    };

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
