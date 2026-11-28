using TradingAgent.Broker;
using TradingAgent.Feed;
using TradingAgent.Models;
using TradingAgent.Reconciliation;

namespace AgentFox.ChannelTests.Simulation;

/// <summary>
/// An in-memory PSX broker for scenario tests: one book, one custody ledger, one activity log, and the
/// venue events the protective-stop machinery has to survive (a halt, the close, a refusal, a part fill,
/// a reply lost after the order already landed, a book read that lags the activity log).
///
/// <para>
/// <b>It encodes only venue rules that are MEASURED</b> (see the cited docs), so a scenario that passes
/// here fails for a reason the real broker would also give:
/// </para>
/// <list type="bullet">
/// <item>A SELL is sized against custody MINUS quantity already committed to resting SELLs, and an excess
/// is refused with the broker's own wording ("You cannot sell more than 0 shares of SYS", 2026-08-27).</item>
/// <item>An armed stop is listed in the outstanding book as <c>STOPLOSS</c> with status <c>APT</c> and
/// commits its shares (2026-09-01). Once triggered it rests as a <c>LIMIT</c> at status <c>QUE</c>, its
/// main number becomes the long exchange id and the short number moves to the alternate column
/// (2026-09-22, see <c>RestingOrder.Is</c>).</item>
/// <item>The venue cancels every resting order at the close, as <c>CLX</c> rows (2026-08-19).</item>
/// <item>The book reports a part-filled order's REMAINING quantity (2026-09-01).</item>
/// </list>
///
/// <para>
/// <b>What a halt does to resting orders is NOT measured.</b> <see cref="Halt"/> takes the owner's
/// stated worst case (2026-11-28): every resting order is cancelled and orders are refused until reopen.
/// </para>
/// </summary>
internal sealed class SimulatedBroker
    : IBrokerAdapter, IBrokerStateReader, IBrokerOrderCanceller, IBrokerMarketState
{
    private static readonly string[] AcceptingStates = ["OPN", "OPEN", "OHO", "PRE"];

    private readonly object _gate = new();
    private readonly Dictionary<string, int> _custody = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, decimal> _prices = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SimOrder> _orders = [];
    private readonly List<BrokerOrderEvent> _activity = [];
    private readonly List<BrokerFill> _fills = [];
    private readonly Queue<(bool Transient, string Text)> _scriptedRejections = new();
    private readonly List<TradingSignal> _submitted = [];

    private int _sequence;
    private int _reads;
    private int _hideNewOrdersForReads;
    private bool _throwAfterNextSend;
    private bool _nextReadUnhealthy;

    // ── IBrokerMarketState ────────────────────────────────────────────────────

    public string? LastMarketStatus { get; private set; } = "OPN";
    public DateTime? LastMarketStatusAtUtc { get; private set; } = DateTime.UtcNow;

    // ── What a test can read back ─────────────────────────────────────────────

    /// <summary>Every signal that reached <see cref="PlaceOrderGroupsAsync"/>, refused or not.</summary>
    public IReadOnlyList<TradingSignal> Submitted { get { lock (_gate) return _submitted.ToList(); } }

    public int PlacementCalls { get { lock (_gate) return _submitted.Count; } }

    public int Custody(string symbol) { lock (_gate) return _custody.GetValueOrDefault(symbol); }

    /// <summary>SELL orders the venue is holding right now, armed or triggered.</summary>
    public IReadOnlyList<SimOrder> RestingSells(string symbol)
    {
        lock (_gate)
            return _orders.Where(o => o.IsResting && o.Side == "SELL"
                                   && o.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))
                .Select(o => o.Clone()).ToList();
    }

    // ── Setting the scene ─────────────────────────────────────────────────────

    public void SetHolding(string symbol, int quantity, decimal price)
    {
        lock (_gate)
        {
            _custody[symbol] = quantity;
            _prices[symbol] = price;
        }
    }

    /// <summary>The next <paramref name="count"/> placements are refused with <paramref name="text"/>.</summary>
    public void RejectNext(int count, string text, bool transient = false)
    {
        lock (_gate)
            for (var i = 0; i < count; i++) _scriptedRejections.Enqueue((transient, text));
    }

    /// <summary>
    /// The next placement LANDS in the book and then the call throws, the way a socket that drops after
    /// the send leaves the caller not knowing the order exists.
    /// </summary>
    public void ThrowAfterNextSend() { lock (_gate) _throwAfterNextSend = true; }

    /// <summary>
    /// Orders placed from now on stay out of the outstanding book for the next <paramref name="reads"/>
    /// snapshots, while still appearing in the activity log at once. That is the real propagation gap
    /// <see cref="BrokerReconciliationSnapshot.OrderEvents"/> exists to close.
    /// </summary>
    public void HideNewOrdersForReads(int reads) { lock (_gate) _hideNewOrdersForReads = reads; }

    public void FailNextRead() { lock (_gate) _nextReadUnhealthy = true; }

    // ── Venue events ──────────────────────────────────────────────────────────

    /// <summary>A market-wide halt, worst case: every resting order is cancelled, new ones are refused.</summary>
    public void Halt()
    {
        lock (_gate)
        {
            SetState("HALT");
            CancelAllLocked();
        }
    }

    public void Reopen() { lock (_gate) SetState("OPN"); }

    /// <summary>The bell: the venue clears the whole book (measured) and stops accepting orders.</summary>
    public void EndOfDay()
    {
        lock (_gate)
        {
            CancelAllLocked();
            SetState("CLO");
        }
    }

    /// <summary>The next session opens. Today's activity and trade logs start empty, as the real ones do.</summary>
    public void NextSession()
    {
        lock (_gate)
        {
            _activity.Clear();
            _fills.Clear();
            SetState("OPN");
        }
    }

    /// <summary>
    /// The market trades at <paramref name="price"/>: armed SELL stops at or above it trigger, and resting
    /// SELL limits at or below it fill in full. A triggered stop whose limit is above the price stays
    /// resting unfilled, which is the stranded case.
    /// </summary>
    public void Trade(string symbol, decimal price)
    {
        lock (_gate)
        {
            _prices[symbol] = price;
            foreach (var order in _orders.Where(o => o.IsResting && o.Side == "SELL"
                         && o.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                if (order.Status == "APT" && price <= order.Trigger)
                {
                    order.Status = "QUE";
                    order.Type = "LIMIT";
                    order.AlternateOrderNo = order.OrderNo;
                    order.OrderNo = "EX" + order.OrderNo;
                    Record(order, "QUE");
                }

                if (order.Status == "QUE" && price >= order.Price)
                    FillLocked(order, order.Remaining, price);
            }
        }
    }

    /// <summary>Fills part of a resting order at its own price, as a thin book would.</summary>
    public void PartialFill(string orderNo, int quantity)
    {
        lock (_gate)
        {
            var order = FindLocked(orderNo) ?? throw new InvalidOperationException($"No order {orderNo}.");
            FillLocked(order, Math.Min(quantity, order.Remaining), order.Price);
        }
    }

    // ── IBrokerAdapter ────────────────────────────────────────────────────────

    public Task<IReadOnlyDictionary<string, decimal?>> GetMarketPricesAsync(IReadOnlyList<string> symbols)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyDictionary<string, decimal?>>(symbols.ToDictionary(
                s => s, s => _prices.TryGetValue(s, out var p) ? (decimal?)p : null,
                StringComparer.OrdinalIgnoreCase));
    }

    public Task<IReadOnlyList<IReadOnlyList<OrderResult>>> PlaceOrderGroupsAsync(
        IReadOnlyList<IReadOnlyList<TradingSignal>> groups)
    {
        var results = new List<IReadOnlyList<OrderResult>>();
        var throwNow = false;

        lock (_gate)
        {
            foreach (var group in groups)
            {
                var row = new List<OrderResult>();
                foreach (var signal in group)
                {
                    _submitted.Add(signal);
                    row.Add(PlaceLocked(signal));
                    if (_throwAfterNextSend)
                    {
                        _throwAfterNextSend = false;
                        throwNow = true;
                    }
                }
                results.Add(row);
            }
        }

        if (throwNow)
            throw new IOException("Simulated socket error after the order was sent.");

        return Task.FromResult<IReadOnlyList<IReadOnlyList<OrderResult>>>(results);
    }

    // ── IBrokerStateReader ────────────────────────────────────────────────────

    public Task<BrokerReconciliationSnapshot> ReadSnapshotAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (_nextReadUnhealthy)
            {
                _nextReadUnhealthy = false;
                return Task.FromResult(new BrokerReconciliationSnapshot(
                    true, false, "Simulated read failure.", now));
            }

            _reads++;
            return Task.FromResult(new BrokerReconciliationSnapshot(true, true, "Simulated broker.", now)
            {
                Positions = _custody.Where(c => c.Value > 0)
                    .Select(c => new BrokerPosition(c.Key, c.Value)).ToList(),
                OpenOrders = _orders.Where(o => o.IsResting && o.VisibleFromRead <= _reads)
                    .Select(o => new BrokerWorkingOrder(
                        o.OrderNo, o.Symbol, o.Side, o.Remaining,
                        o.Status == "APT" ? o.Trigger : o.Price, o.Type, o.AlternateOrderNo))
                    .ToList(),
                OrderEvents = _activity.ToList(),
                Fills = _fills.ToList()
            });
        }
    }

    // ── IBrokerOrderCanceller ─────────────────────────────────────────────────

    public Task<BrokerCancellationResult> CancelOrderAsync(string orderNo, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var order = FindLocked(orderNo);
            if (order is null || !order.IsResting)
                return Task.FromResult(new BrokerCancellationResult(
                    Gone: true, RequestAccepted: false, Verified: true,
                    $"Order {orderNo} is not in the book."));

            order.Status = "CLX";
            Record(order, "CLX");
            return Task.FromResult(new BrokerCancellationResult(
                Gone: true, RequestAccepted: true, Verified: true, $"Order {orderNo} cancelled."));
        }
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private OrderResult PlaceLocked(TradingSignal signal)
    {
        var symbol = signal.Symbol.Trim().ToUpperInvariant();
        var side = signal.Action.Trim().ToUpperInvariant();
        var type = signal.OrderType.Trim().ToUpperInvariant();
        var quantity = signal.Quantity ?? 0;

        OrderResult Refused(string message, bool transient) => new()
        {
            Success = false, Action = side, Symbol = symbol, Message = message,
            TransientRejection = transient, RequestedPrice = signal.EntryPrice
        };

        if (!AcceptingStates.Contains(LastMarketStatus ?? "", StringComparer.OrdinalIgnoreCase))
            return Refused($"Market is in {LastMarketStatus} state; the order was not accepted.", transient: true);

        if (_scriptedRejections.TryDequeue(out var scripted))
            return Refused(scripted.Text, scripted.Transient);

        if (side == "SELL")
        {
            var committed = _orders.Where(o => o.IsResting && o.Side == "SELL"
                                            && o.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))
                .Sum(o => o.Remaining);
            var free = _custody.GetValueOrDefault(symbol) - committed;
            if (quantity > free)
                return Refused($"You cannot sell more than {Math.Max(0, free)} shares of {symbol}", transient: false);
        }

        var order = new SimOrder
        {
            OrderNo = $"0411XK{++_sequence}",
            Symbol = symbol,
            Side = side,
            Type = type,
            Quantity = quantity,
            Remaining = quantity,
            Trigger = signal.EntryPrice ?? 0m,
            Price = type == "STOPLOSS" ? signal.LimitPrice ?? signal.EntryPrice ?? 0m : signal.EntryPrice ?? 0m,
            Status = type == "STOPLOSS" ? "APT" : "QUE",
            VisibleFromRead = _reads + 1 + _hideNewOrdersForReads
        };
        _orders.Add(order);
        Record(order, order.Status);

        if (type == "MARKET")
            FillLocked(order, quantity, _prices.GetValueOrDefault(symbol));

        return new OrderResult
        {
            Success = true, OrderId = order.OrderNo, Action = side, Symbol = symbol,
            Quantity = quantity, RequestedQuantity = quantity,
            RequestedPrice = signal.EntryPrice, SubmittedPrice = signal.EntryPrice,
            Message = "Simulated broker accepted the order."
        };
    }

    private void FillLocked(SimOrder order, int quantity, decimal price)
    {
        if (quantity <= 0) return;
        order.Remaining -= quantity;
        if (order.Remaining == 0) order.Status = "TRD";

        var delta = order.Side == "SELL" ? -quantity : quantity;
        _custody[order.Symbol] = _custody.GetValueOrDefault(order.Symbol) + delta;
        _fills.Add(new BrokerFill(order.AlternateOrderNo ?? order.OrderNo, order.Symbol, order.Side,
            quantity, price, DateTime.UtcNow));
        _activity.Add(new BrokerOrderEvent(order.OrderNo, order.Symbol, order.Side, "TRD",
            quantity, price, DateTime.UtcNow));
    }

    private void CancelAllLocked()
    {
        foreach (var order in _orders.Where(o => o.IsResting))
        {
            order.Status = "CLX";
            Record(order, "CLX");
        }
    }

    private SimOrder? FindLocked(string orderNo) =>
        _orders.FirstOrDefault(o => string.Equals(o.OrderNo, orderNo, StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(o.AlternateOrderNo, orderNo, StringComparison.OrdinalIgnoreCase));

    private void Record(SimOrder order, string status) =>
        _activity.Add(new BrokerOrderEvent(order.OrderNo, order.Symbol, order.Side, status,
            order.Remaining, order.Status == "APT" ? order.Trigger : order.Price, DateTime.UtcNow));

    private void SetState(string state)
    {
        LastMarketStatus = state;
        LastMarketStatusAtUtc = DateTime.UtcNow;
    }
}

/// <summary>One order as the simulated venue holds it.</summary>
internal sealed class SimOrder
{
    public required string OrderNo { get; set; }
    public string? AlternateOrderNo { get; set; }
    public required string Symbol { get; init; }
    public required string Side { get; init; }
    public required string Type { get; set; }
    public int Quantity { get; init; }
    public int Remaining { get; set; }
    public decimal Trigger { get; init; }
    public decimal Price { get; init; }

    /// <summary><c>APT</c> armed stop, <c>QUE</c> resting limit, <c>CLX</c> cancelled, <c>TRD</c> filled.</summary>
    public required string Status { get; set; }

    /// <summary>The first snapshot number that may list this order; see <c>HideNewOrdersForReads</c>.</summary>
    public int VisibleFromRead { get; init; }

    public bool IsResting => Status is "APT" or "QUE";

    public SimOrder Clone() => (SimOrder)MemberwiseClone();
}
