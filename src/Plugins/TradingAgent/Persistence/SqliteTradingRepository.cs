using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Manager;

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
                CREATE INDEX IF NOT EXISTS ix_trading_order_events_execution
                    ON trading_order_events(execution_id, event_id);
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
}
