using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Models;
using TradingAgent.Persistence;
using TradingAgent.Reconciliation;

namespace AgentFox.ChannelTests;

/// <summary>
/// Reading back what a position actually sold for.
///
/// <para>
/// <b>Why this needs testing rather than reading.</b> The <c>fills</c> table stores only quantity,
/// price and time — the symbol and side live in the parent <c>broker_orders</c> row as JSON, and that
/// JSON is one of two unrelated shapes depending on whether this system placed the order or
/// reconciliation observed it. The query pulls the side from <c>Action</c> in one and <c>Side</c> in
/// the other. Both paths are exercised here against genuinely serialized records, because the whole
/// mechanism rests on an assumption about field names that a refactor elsewhere could silently break
/// — and the failure mode is a position that quietly stops being measurable.
/// </para>
/// </summary>
[TestClass]
public sealed class RecordedFillLookupTests
{
    private string _root = "";
    private static readonly DateTime Base = new(2026, 8, 10, 10, 0, 0, DateTimeKind.Utc);

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "agentfox-fills-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private SqliteTradingRepository NewRepository()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Workspaces:0"] = _root })
            .Build();

        return new SqliteTradingRepository(
            Options.Create(new TradingAgentOptions { DatabasePath = "trading/trading.db" }),
            configuration,
            NullLogger<SqliteTradingRepository>.Instance);
    }

    /// <summary>The reconciliation path: a fill the broker reported for an order we may not have placed.</summary>
    private static BrokerFill Observed(
        string orderNo, string symbol, string side, int quantity, decimal price, int dayOffset) =>
        new(orderNo, symbol, side, quantity, price, Base.AddDays(dayOffset));

    [TestMethod]
    public async Task A_fill_observed_by_reconciliation_is_readable_with_its_symbol_and_side()
    {
        var repository = NewRepository();

        await repository.RecordFillsAsync([Observed("A1", "OGDC", "SELL", 100, 110m, 5)]);

        var fills = await repository.GetFillsForSymbolAsync("OGDC", Base);

        Assert.AreEqual(1, fills.Count);
        Assert.AreEqual("OGDC", fills[0].Symbol);
        Assert.AreEqual("SELL", fills[0].Side);
        Assert.AreEqual(100, fills[0].Quantity);
        Assert.AreEqual(110m, fills[0].Price);
    }

    /// <summary>
    /// The other JSON shape. An order this system placed stores an <c>OrderResult</c>, whose side
    /// field is <c>Action</c> — so a query that only knew about <c>Side</c> would silently return a
    /// null side here and the fill would be discarded as unattributable.
    /// </summary>
    [TestMethod]
    public async Task A_fill_against_an_order_this_system_placed_recovers_its_side_from_Action()
    {
        var repository = NewRepository();

        // broker_orders.execution_id is a foreign key, so the execution has to exist first — the
        // same order the real placement path follows.
        var claim = await repository.TryBeginExecutionAsync("idem-1", "{}", "test");
        Assert.IsTrue(claim.Acquired);

        await repository.RecordBrokerOrdersAsync(claim.ExecutionId, [
            new OrderResult
            {
                Success = true,
                OrderId = "B7",
                Action = "SELL",
                Symbol = "OGDC",
                Message = "placed"
            }
        ]);
        await repository.RecordFillsAsync([Observed("B7", "OGDC", "SELL", 50, 108m, 4)]);

        var fills = await repository.GetFillsForSymbolAsync("OGDC", Base);

        Assert.AreEqual(1, fills.Count);
        Assert.AreEqual("SELL", fills[0].Side,
            "The side must be recovered from OrderResult.Action, not only from BrokerFill.Side.");
        Assert.AreEqual(50, fills[0].Quantity);
    }

    [TestMethod]
    public async Task Buys_and_sells_are_both_returned_and_distinguishable()
    {
        var repository = NewRepository();

        await repository.RecordFillsAsync([
            Observed("A1", "OGDC", "BUY", 100, 100m, 0),
            Observed("A2", "OGDC", "SELL", 100, 110m, 5)
        ]);

        var fills = await repository.GetFillsForSymbolAsync("OGDC", Base);

        Assert.AreEqual(2, fills.Count);
        Assert.AreEqual(1, fills.Count(f => f.Side == "BUY"));
        Assert.AreEqual(1, fills.Count(f => f.Side == "SELL"));
    }

    [TestMethod]
    public async Task Another_symbols_fills_are_not_returned()
    {
        var repository = NewRepository();

        await repository.RecordFillsAsync([
            Observed("A1", "OGDC", "SELL", 100, 110m, 5),
            Observed("A2", "NETSOL", "SELL", 200, 120m, 5)
        ]);

        var fills = await repository.GetFillsForSymbolAsync("OGDC", Base);

        Assert.AreEqual(1, fills.Count);
        Assert.AreEqual("OGDC", fills[0].Symbol);
    }

    /// <summary>
    /// The window matters: a campaign measures only its own lifetime, so an earlier position in the
    /// same symbol must not leak into it and turn one trade's result into two trades' worth.
    /// </summary>
    [TestMethod]
    public async Task Fills_from_before_the_window_are_excluded()
    {
        var repository = NewRepository();

        await repository.RecordFillsAsync([
            Observed("OLD", "OGDC", "SELL", 999, 90m, -30),
            Observed("NEW", "OGDC", "SELL", 100, 110m, 5)
        ]);

        var fills = await repository.GetFillsForSymbolAsync("OGDC", Base);

        Assert.AreEqual(1, fills.Count);
        Assert.AreEqual(100, fills[0].Quantity);
    }

    [TestMethod]
    public async Task Fills_are_returned_oldest_first()
    {
        var repository = NewRepository();

        await repository.RecordFillsAsync([
            Observed("A2", "OGDC", "SELL", 100, 110m, 5),
            Observed("A1", "OGDC", "BUY", 100, 100m, 0)
        ]);

        var fills = await repository.GetFillsForSymbolAsync("OGDC", Base);

        Assert.AreEqual("BUY", fills[0].Side, "The purchase came first and must be listed first.");
        Assert.AreEqual("SELL", fills[1].Side);
    }

    [TestMethod]
    public async Task A_symbol_lookup_is_case_insensitive()
    {
        var repository = NewRepository();
        await repository.RecordFillsAsync([Observed("A1", "OGDC", "SELL", 100, 110m, 5)]);

        Assert.AreEqual(1, (await repository.GetFillsForSymbolAsync("ogdc", Base)).Count);
    }

    [TestMethod]
    public async Task A_symbol_with_no_fills_returns_an_empty_list_rather_than_failing()
    {
        var repository = NewRepository();

        var fills = await repository.GetFillsForSymbolAsync("NOTHING", Base);

        Assert.AreEqual(0, fills.Count);
    }

    /// <summary>
    /// Several partial fills against one order — the ordinary shape of a large sale on PSX — must all
    /// come back, or the proceeds are understated and the trade looks worse than it was.
    /// </summary>
    [TestMethod]
    public async Task Multiple_partial_fills_on_one_order_are_all_returned()
    {
        var repository = NewRepository();

        await repository.RecordFillsAsync([
            Observed("A1", "OGDC", "SELL", 40, 110m, 5),
            Observed("A1", "OGDC", "SELL", 60, 111m, 5).Round()
        ]);

        var fills = await repository.GetFillsForSymbolAsync("OGDC", Base);

        Assert.AreEqual(2, fills.Count);
        Assert.AreEqual(100, fills.Sum(f => f.Quantity));
    }
}

/// <summary>
/// Nudges a fill's timestamp so two partials on the same order do not collide on the fill id, which
/// is built from order, time and quantity.
/// </summary>
internal static class BrokerFillTestExtensions
{
    public static BrokerFill Round(this BrokerFill fill) =>
        fill with { FilledUtc = fill.FilledUtc.AddMinutes(1) };
}
