using System.Globalization;
using Microsoft.Data.Sqlite;
using TradingAgent.Watchlist;

namespace TradingAgent.Persistence;

/// <summary>The per-order ledger behind live stop coverage. See <see cref="IProtectiveStopOrderLedger"/>.</summary>
public sealed partial class SqliteTradingRepository : IProtectiveStopOrderLedger
{
    public async Task<IReadOnlyList<ProtectiveStopOrder>> GetProtectiveStopOrdersAsync(
        DateOnly sessionDate, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, stop_id, session_date, order_no, qty, placed_utc, state, first_absent_utc, last_seen_utc
              FROM protective_stop_orders
             WHERE session_date = $date
             ORDER BY id
            """;
        command.Parameters.AddWithValue("$date", sessionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        var rows = new List<ProtectiveStopOrder>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new ProtectiveStopOrder(
                reader.GetString(1),
                DateOnly.ParseExact(reader.GetString(2), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4),
                ParseUtc(reader.GetString(5)),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : ParseUtc(reader.GetString(7)),
                reader.IsDBNull(8) ? null : ParseUtc(reader.GetString(8))));
        }
        return rows;
    }

    public async Task<bool> RecordProtectiveStopOrderAsync(
        ProtectiveStopOrder order, int placedTotal, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        var update = connection.CreateCommand();
        update.Transaction = transaction;
        // placed_qty is SET, not added to: the caller passes what still covered before this order plus what it
        // placed. Adding would count an order that already left the book alongside the one replacing it.
        // last_order_no always follows the newest order — a placement with no number clears it rather than
        // leaving the previous number to stand for an order it does not name.
        update.CommandText = """
            UPDATE protective_stops
               SET placed_qty = $total,
                   last_placed_date = $date,
                   last_order_no = $orderNo
             WHERE stop_id = $id AND state = 'active'
            """;
        update.Parameters.AddWithValue("$id", order.StopId);
        update.Parameters.AddWithValue("$total", Math.Max(0, placedTotal));
        update.Parameters.AddWithValue("$date", order.SessionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        update.Parameters.AddWithValue("$orderNo", (object?)order.OrderNo ?? DBNull.Value);
        if (await update.ExecuteNonQueryAsync(ct) != 1)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }

        var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO protective_stop_orders (stop_id, session_date, order_no, qty, placed_utc, state)
            VALUES ($id, $date, $orderNo, $qty, $placed, 'resting')
            """;
        insert.Parameters.AddWithValue("$id", order.StopId);
        insert.Parameters.AddWithValue("$date", order.SessionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        insert.Parameters.AddWithValue("$orderNo", (object?)order.OrderNo ?? DBNull.Value);
        insert.Parameters.AddWithValue("$qty", order.Quantity);
        insert.Parameters.AddWithValue("$placed", order.PlacedUtc.ToString("O", CultureInfo.InvariantCulture));
        await insert.ExecuteNonQueryAsync(ct);

        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task UpdateProtectiveStopOrdersAsync(
        IReadOnlyList<ProtectiveStopOrder> orders, CancellationToken ct = default)
    {
        if (orders.Count == 0) return;
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        foreach (var order in orders)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            // Matched on the stop, the session, the number and the placement time together: one stop may place
            // the same number twice on a day only across reconnects (numbers restart per connection), and the
            // placement time separates those.
            command.CommandText = """
                UPDATE protective_stop_orders
                   SET state = $state, first_absent_utc = $absent, last_seen_utc = $seen
                 WHERE stop_id = $id AND session_date = $date AND placed_utc = $placed
                   AND (order_no = $orderNo OR (order_no IS NULL AND $orderNo IS NULL))
                """;
            command.Parameters.AddWithValue("$state", order.State);
            command.Parameters.AddWithValue("$absent",
                (object?)order.FirstAbsentUtc?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
            command.Parameters.AddWithValue("$seen",
                (object?)order.LastSeenUtc?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
            command.Parameters.AddWithValue("$id", order.StopId);
            command.Parameters.AddWithValue("$date", order.SessionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$placed", order.PlacedUtc.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$orderNo", (object?)order.OrderNo ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    public async Task<int> PruneProtectiveStopOrdersAsync(DateOnly before, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM protective_stop_orders WHERE session_date < $before";
        command.Parameters.AddWithValue("$before", before.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return await command.ExecuteNonQueryAsync(ct);
    }
}
