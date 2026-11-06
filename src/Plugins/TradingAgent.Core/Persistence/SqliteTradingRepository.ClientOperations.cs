using Microsoft.Data.Sqlite;

namespace TradingAgent.Persistence;

public sealed partial class SqliteTradingRepository : IClientModificationOperationRepository
{
    // Nullable columns on trading_executions share that row's retention. Terminal rows cannot be
    // swept before operation expiry; submitting/unknown retain their existing explicit-resolution rule.
    public async Task<ClientOrderOperation?> FindClientOrderOperationAsync(string requestId, CancellationToken ct = default)
    {
        if (!ClientOrderRequestId.TryGetExpiry(requestId, out _)) throw new ArgumentException("Invalid client request ID.", nameof(requestId));
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        return await FindClientOperationAsync(connection, null, requestId, ct);
    }

    public Task<ClientOrderOperationClaim> TryBeginClientOrderOperationAsync(string requestId, string requestHash,
        string effectiveRequestJson, string policyVersion, CancellationToken ct = default) =>
        Begin("order", requestId, requestHash, effectiveRequestJson, policyVersion, ct);

    public Task<ClientOrderOperationClaim> TryBeginClientActionOperationAsync(string kind, string requestId, string requestHash,
        string requestJson, string policyVersion, CancellationToken ct = default) => kind is "cancel" or "modify"
            ? Begin(kind, requestId, requestHash, requestJson, policyVersion, ct)
            : throw new ArgumentException("Unsupported client action.", nameof(kind));

    public async Task<bool> CompareExchangeModificationResultAsync(string requestId, string expectedResultJson,
        string resultJson, CancellationToken ct = default)
    {
        if (!ClientOrderRequestId.TryGetExpiry(requestId, out _) || expectedResultJson is not { Length: > 0 and <= 16_384 }
            || resultJson is not { Length: > 0 and <= 16_384 } || System.Text.Encoding.UTF8.GetByteCount(resultJson) > 16_384)
            throw new ArgumentException("Invalid modification progress.");
        await EnsureInitializedAsync(ct); await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand(); command.CommandText = """
            UPDATE trading_executions SET result_json=$result, updated_utc=$now
            WHERE client_operation_id=$id AND client_operation_kind='modify' AND state='accepted' AND result_json=$expected
            """;
        command.Parameters.AddWithValue("$result", resultJson); command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", requestId); command.Parameters.AddWithValue("$expected", expectedResultJson);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    private async Task<ClientOrderOperationClaim> Begin(string kind, string requestId, string requestHash,
        string effectiveRequestJson, string policyVersion, CancellationToken ct)
    {
        if (!ClientOrderRequestId.TryGetExpiry(requestId, out var expiry)) throw new ArgumentException("Invalid client request ID.", nameof(requestId));
        if (requestHash is not { Length: 64 } || requestHash.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("Invalid request fingerprint.", nameof(requestHash));
        requestHash = requestHash.ToUpperInvariant();
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        var inserted = false;
        if (ClientOrderRequestId.CanStart(requestId, DateTime.UtcNow))
        {
            // INSERT first, as in the existing claim: concurrent SQLite writers must not both take
            // read locks and then try to upgrade them. The unique operation index chooses one owner.
            var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO trading_executions
                    (execution_id, idempotency_key, state, request_json, policy_version, created_utc, updated_utc,
                     correlation_id, client_operation_id, client_request_hash, client_operation_expires_utc, client_operation_kind)
                VALUES ($id, $key, 'submitting', $request, $policy, $now, $now, $correlation, $operation, $hash, $expiry, $kind)
                """;
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            command.Parameters.AddWithValue("$key", "client-order:v1:" + requestId);
            command.Parameters.AddWithValue("$request", effectiveRequestJson);
            command.Parameters.AddWithValue("$policy", policyVersion);
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$correlation", CorrelationParameter());
            command.Parameters.AddWithValue("$operation", requestId);
            command.Parameters.AddWithValue("$hash", requestHash);
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$expiry", expiry.ToString("O"));
            inserted = await command.ExecuteNonQueryAsync(ct) == 1;
        }
        var operation = await FindClientOperationAsync(connection, transaction, requestId, ct);
        if (operation is null) throw new InvalidOperationException("Client request ID expired, is in the future, or could not be claimed.");
        await transaction.CommitAsync(ct);
        return new(inserted, operation.RequestHash != requestHash || operation.Kind != kind, operation);
    }

    private static async Task<ClientOrderOperation?> FindClientOperationAsync(SqliteConnection connection, SqliteTransaction? transaction,
        string requestId, CancellationToken ct)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            SELECT client_operation_id, client_request_hash, execution_id, state, policy_version, result_json,
                   created_utc, updated_utc, client_operation_expires_utc, client_operation_kind, request_json
            FROM trading_executions WHERE client_operation_id = $operation
            """;
        command.Parameters.AddWithValue("$operation", requestId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5), ParseUtc(reader.GetString(6)), ParseUtc(reader.GetString(7)), ParseUtc(reader.GetString(8)), reader.GetString(9), reader.GetString(10));
    }
}
