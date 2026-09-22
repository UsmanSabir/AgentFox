using TradingAgent.Watchlist;

namespace AgentFox.ChannelTests;

/// <summary>
/// Recognising a stop that TRIGGERED and was never filled — the price fell through its limit and left
/// a sell order sitting above the market.
///
/// <para>
/// What follows from a positive answer is a cancel and, for an opted-in stop, a sale at market at an
/// unknown price. So these tests lean on the cases where answering yes would be WRONG: a stop that has
/// not triggered, an order that is not ours, a book that could not be read, a type nobody reported.
/// </para>
/// </summary>
[TestClass]
public sealed class StrandedStopTests
{
    private static readonly DateOnly Today = new(2026, 9, 22);

    private static ProtectiveStop Stop() => new()
    {
        StopId = "s1",
        Symbol = "PAEL",
        StopTrigger = 38.55m,
        StopLimit = 38.40m,
        DesiredQuantity = 10,
        State = "active",
        PlacedQuantity = 10,
        LastPlacedSessionDate = Today,
        LastOrderNo = "0411XK65"
    };

    private static RestingOrder Row(string? type, string? orderNo, string? alternate = null,
        string symbol = "PAEL", string? side = "SEL") =>
        new(symbol, side, type, 10, 38.40m, orderNo, "", alternate);

    [TestMethod]
    public void A_triggered_stop_resting_as_a_limit_is_stranded()
    {
        var found = ProtectiveStopDecisions.FindStrandedLimit(Stop(), [Row("LIMIT", "0411XK65")], Today);

        Assert.IsNotNull(found);
    }

    [TestMethod]
    public void A_stop_that_has_not_triggered_is_not_stranded()
    {
        Assert.IsNull(ProtectiveStopDecisions.FindStrandedLimit(
            Stop(), [Row("STOPLOSS", "0411XK65")], Today),
            "an armed stop waiting for its trigger is doing exactly its job");
    }

    [TestMethod]
    public void The_short_number_is_found_in_either_column()
    {
        // MEASURED 2026-09-22: the short number stays in field 6 and field 5 becomes the long exchange
        // id when a stop triggers. Both placements are still accepted, because the one case — a stop
        // caught between triggering and filling — has never been captured in the book itself.
        Assert.IsNotNull(ProtectiveStopDecisions.FindStrandedLimit(
            Stop(), [Row("LIMIT", "0010TKNSH300GM0R", alternate: "0411XK65")], Today));
    }

    [TestMethod]
    public void A_triggered_stop_is_still_recognised_as_ours_everywhere_it_is_asked()
    {
        // The book's main-id column holds the LONG exchange id once a stop has triggered, and the
        // short number we recorded moves to the second column. Every "is this my order" test must see
        // through that — RetireSupersededAsync reading it as gone would close the row without
        // cancelling, leaving a live sell resting over the position.
        var triggered = Row("LIMIT", "0010TKNSH300GM0R", alternate: "0411XK65");

        Assert.IsTrue(triggered.Is("0411XK65"), "matched by the short number in the second column");
        Assert.IsTrue(triggered.Is("0010TKNSH300GM0R"), "and by the venue's primary id");
        Assert.IsFalse(triggered.Is("0411XK99"));
        Assert.IsFalse(triggered.Is(null));
        Assert.IsTrue(triggered.IsAnyOf(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "0411XK65" }),
            "an exclusion set naming our number must exclude the triggered row too");
        Assert.IsFalse(triggered.IsAnyOf(new HashSet<string>()));
    }

    [TestMethod]
    public void An_order_is_never_matched_by_price_alone()
    {
        Assert.IsNull(ProtectiveStopDecisions.FindStrandedLimit(
            Stop(), [Row("LIMIT", "0911XK7")], Today),
            "a limit at the same price with a different number is somebody else's order — cancelling "
            + "it and selling at market would act on an order this stop never placed");
    }

    [TestMethod]
    public void The_same_number_on_another_symbol_is_not_ours()
    {
        // Short numbers are unique only per connection; 0411XK1 has named three different orders.
        Assert.IsNull(ProtectiveStopDecisions.FindStrandedLimit(
            Stop(), [Row("LIMIT", "0411XK65", symbol: "MARI")], Today));
    }

    [TestMethod]
    public void A_buy_is_never_a_stranded_sell_stop()
    {
        Assert.IsNull(ProtectiveStopDecisions.FindStrandedLimit(
            Stop(), [Row("LIMIT", "0411XK65", side: "BUY")], Today));
    }

    [TestMethod]
    public void Unknown_never_counts()
    {
        Assert.IsNull(ProtectiveStopDecisions.FindStrandedLimit(Stop(), null, Today),
            "an unreadable book is not evidence of anything");
        Assert.IsNull(ProtectiveStopDecisions.FindStrandedLimit(
            Stop(), [Row(null, "0411XK65")], Today),
            "a broker that reports no order type cannot tell us the stop triggered");
    }

    [TestMethod]
    public void A_stop_placed_on_an_earlier_session_is_not_judged_on_todays_book()
    {
        var yesterday = Stop() with { LastPlacedSessionDate = Today.AddDays(-1) };

        Assert.IsNull(ProtectiveStopDecisions.FindStrandedLimit(
            yesterday, [Row("LIMIT", "0411XK65")], Today),
            "the venue clears the book at the close; a number from yesterday names nothing today");
    }

    [TestMethod]
    public void A_stop_that_is_not_active_is_not_stranded()
    {
        Assert.IsNull(ProtectiveStopDecisions.FindStrandedLimit(
            Stop() with { State = "superseded_pending_cancel" }, [Row("LIMIT", "0411XK65")], Today),
            "a superseded stop's order is being retired by its own path");
    }
}
