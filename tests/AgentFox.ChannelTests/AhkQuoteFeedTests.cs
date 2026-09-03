using Microsoft.Extensions.Logging.Abstractions;
using TradingAgent.Feed;
using TradingAgent.Research;

namespace AgentFox.ChannelTests;

/// <summary>
/// The AHK live-feed mapping, book, and source-merge rules.
///
/// <para>
/// The assertions here are mostly about what must NOT happen. The portal publishes 0.00 for an
/// untraded symbol, the feed may or may not be a delta stream, and a dead feed is indistinguishable
/// from a quiet market — each of those turns into a wrong PRICE rather than an error if it is
/// mishandled, and a wrong price is what a stop-loss acts on.
/// </para>
/// </summary>
[TestClass]
public sealed class AhkQuoteFeedTests
{
    private static readonly DateTime Now = new(2026, 8, 18, 9, 0, 0, DateTimeKind.Utc);

    // ── Mapping ───────────────────────────────────────────────────────────────

    [TestMethod]
    public void ZeroPrices_MapToUnknown_NotToZero()
    {
        // Exactly what the portal sends for a symbol that has not traded today.
        var raw = new AhkFeedQuote
        {
            Mkt = "REG", Symbol = "OGDC",
            LastPrice = 0m, OpenPrice = 0m, High = 0m, Low = 0m, ClosePrice = 0m,
            Buy = 0m, Sell = 0m, TotalVolume = 0
        };

        var quote = AhkQuoteMapper.ToLiveQuote(raw, Now);

        Assert.IsNotNull(quote);
        Assert.IsNull(quote.Current, "A zero last price means 'has not traded', never a price of zero.");
        Assert.IsNull(quote.Open);
        Assert.IsNull(quote.High);
        Assert.IsNull(quote.Low);
        Assert.IsNull(quote.PreviousClose);
        Assert.IsNull(quote.BestBid);
        Assert.IsNull(quote.BestAsk);
        Assert.AreEqual(0L, quote.Volume, "Zero VOLUME is a real observation, unlike a zero price.");
    }

    [TestMethod]
    public void AbsoluteChange_IsConvertedToPercent_AgainstPreviousClose()
    {
        var raw = new AhkFeedQuote
        {
            Mkt = "REG", Symbol = "PPL",
            LastPrice = 110m, ClosePrice = 100m, Change = 10m
        };

        var quote = AhkQuoteMapper.ToLiveQuote(raw, Now);

        Assert.IsNotNull(quote);
        Assert.AreEqual(10m, quote.ChangePercent,
            "The portal publishes an absolute change; consumers here all expect a percentage.");
    }

    [TestMethod]
    public void ChangePercent_IsNull_WhenPreviousCloseIsMissing()
    {
        var raw = new AhkFeedQuote { Mkt = "REG", Symbol = "PPL", LastPrice = 110m, ClosePrice = 0m, Change = 10m };

        var quote = AhkQuoteMapper.ToLiveQuote(raw, Now);

        Assert.IsNotNull(quote);
        Assert.IsNull(quote.ChangePercent,
            "Without a real previous close the change is unattributable — it must not be invented "
            + "from the open or the last price, and it must never divide by zero.");
    }

    [TestMethod]
    public void BidAndAsk_SurviveMapping_AndProduceASpread()
    {
        var raw = new AhkFeedQuote
        {
            Mkt = "REG", Symbol = "OGDC",
            LastPrice = 100m, Buy = 99.5m, BVol = 1000, Sell = 100.5m, SVol = 800
        };

        var quote = AhkQuoteMapper.ToLiveQuote(raw, Now);

        Assert.IsNotNull(quote);
        Assert.AreEqual(99.5m, quote.BestBid);
        Assert.AreEqual(1000L, quote.BestBidSize);
        Assert.AreEqual(100.5m, quote.BestAsk);
        Assert.AreEqual(800L, quote.BestAskSize);
        Assert.AreEqual(1.0m, quote.Spread, "Depth is the whole point of preferring the broker feed.");
        Assert.AreEqual("ahk", quote.Source);
    }

    // ── The book ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void Book_AccumulatesAcrossPolls_SoADeltaFeedDoesNotLoseSymbols()
    {
        // This is the property that makes the book correct whether GetFeed is a snapshot or a delta
        // queue — which could not be established against the live portal.
        var book = new AhkQuoteBook();

        book.Apply([Quote("OGDC", 100m)], Now);
        book.Apply([Quote("PPL", 200m)], Now.AddSeconds(2));

        var snapshot = book.Snapshot("REG", TimeSpan.FromMinutes(10), Now.AddSeconds(3));

        Assert.AreEqual(2, snapshot.Count,
            "A symbol that did not tick in the latest poll must not vanish from the book.");
        Assert.AreEqual(100m, snapshot["OGDC"].Current);
        Assert.AreEqual(200m, snapshot["PPL"].Current);
    }

    [TestMethod]
    public void Book_MergesPartialUpdates_RatherThanBlankingKnownFields()
    {
        var book = new AhkQuoteBook();

        book.Apply([new AhkFeedQuote
        {
            Mkt = "REG", Symbol = "OGDC",
            LastPrice = 100m, High = 105m, Low = 95m, ClosePrice = 99m
        }], Now);

        // A later message carrying only a bid change.
        book.Apply([new AhkFeedQuote { Mkt = "REG", Symbol = "OGDC", Buy = 99.9m }], Now.AddSeconds(2));

        var quote = book.Snapshot("REG", TimeSpan.FromMinutes(10), Now.AddSeconds(3))["OGDC"];

        Assert.AreEqual(99.9m, quote.BestBid);
        Assert.AreEqual(105m, quote.High, "A partial update must not erase the session high.");
        Assert.AreEqual(95m, quote.Low);
        Assert.AreEqual(100m, quote.Current);
    }

    [TestMethod]
    public void APricelessUpdate_DoesNotRefreshThePricesAge()
    {
        // The portal republishes a symbol that has not traded: the message carries a bid but no last
        // price, so Merge keeps the old Current. It must NOT also declare that old price to be new.
        //
        // It used to. AhkQuoteBook.Snapshot expires on RetrievedAtUtc, so a quiet symbol had its clock
        // reset by every poll — 2s by default — and MaxQuoteAgeSeconds could never reach it. An
        // arbitrarily old price was handed to armed-order evaluation as a current one, and the only
        // thing that could have said otherwise (LastTradeTime) is read by no gate in the repo.
        var book = new AhkQuoteBook();
        book.Apply([new AhkFeedQuote { Mkt = "REG", Symbol = "OGDC", LastPrice = 100m }], Now);

        // Eleven minutes of the portal saying "still here", never "it traded".
        for (var second = 120; second <= 660; second += 120)
            book.Apply(
                [new AhkFeedQuote { Mkt = "REG", Symbol = "OGDC", Buy = 99.9m }],
                Now.AddSeconds(second));

        var snapshot = book.Snapshot("REG", TimeSpan.FromMinutes(10), Now.AddSeconds(661));

        Assert.IsFalse(snapshot.ContainsKey("OGDC"),
            "the price is eleven minutes old, so a ten-minute freshness bound must drop it — being "
            + "told about the symbol is not the same as being told what it costs");
    }

    [TestMethod]
    public void ARealTradeStillRefreshesTheAge()
    {
        // The other half: a message that DOES carry a price is exactly what should reset the clock,
        // or the fix above would expire live symbols mid-session.
        var book = new AhkQuoteBook();
        book.Apply([new AhkFeedQuote { Mkt = "REG", Symbol = "OGDC", LastPrice = 100m }], Now);
        book.Apply([new AhkFeedQuote { Mkt = "REG", Symbol = "OGDC", LastPrice = 101m }],
            Now.AddSeconds(660));

        var snapshot = book.Snapshot("REG", TimeSpan.FromMinutes(10), Now.AddSeconds(661));

        Assert.IsTrue(snapshot.ContainsKey("OGDC"));
        Assert.AreEqual(101m, snapshot["OGDC"].Current);
    }

    [TestMethod]
    public void Book_DropsStaleQuotes_SoADeadFeedDoesNotServeOldPrices()
    {
        var book = new AhkQuoteBook();
        book.Apply([Quote("OGDC", 100m)], Now);

        var fresh = book.Snapshot("REG", TimeSpan.FromMinutes(10), Now.AddMinutes(5));
        var stale = book.Snapshot("REG", TimeSpan.FromMinutes(10), Now.AddMinutes(30));

        Assert.AreEqual(1, fresh.Count);
        Assert.AreEqual(0, stale.Count,
            "A silently dead feed looks exactly like a quiet market; serving its last price forever "
            + "is how a stop is evaluated against an hours-old number.");
    }

    [TestMethod]
    public void Book_SeparatesBoards_SoOddLotPricesNeverLeakIntoRegular()
    {
        var book = new AhkQuoteBook();
        book.Apply([Quote("OGDC", 100m, market: "REG"), Quote("OGDC", 88m, market: "ODL")], Now);

        var regular = book.Snapshot("REG", TimeSpan.FromMinutes(10), Now);

        Assert.AreEqual(1, regular.Count);
        Assert.AreEqual(100m, regular["OGDC"].Current,
            "The odd-lot board is a different order book, not a second opinion on the same one.");
    }

    // ── Composite merge ───────────────────────────────────────────────────────

    [TestMethod]
    public async Task Composite_FillsGapsFromLowerPriority_RatherThanFailingOver()
    {
        // The broker feed covers only what it subscribed; PSX covers the whole market. A failover
        // would silently drop every symbol the broker feed does not carry.
        var ahk = new StubSource("ahk", ("OGDC", 100m));
        var psx = new StubSource("psx", ("OGDC", 99m), ("PPL", 200m), ("HBL", 150m));

        var composite = new CompositeLiveQuoteSource([ahk, psx],
            NullLogger<CompositeLiveQuoteSource>.Instance);

        var snapshot = await composite.GetQuotesAsync();

        Assert.AreEqual(3, snapshot.Quotes.Count);
        Assert.AreEqual(100m, snapshot.Quotes["OGDC"].Current, "The higher-priority source wins a contested symbol.");
        Assert.AreEqual("ahk", snapshot.Quotes["OGDC"].Source);
        Assert.AreEqual("psx", snapshot.Quotes["PPL"].Source, "Provenance is per symbol, not per snapshot.");
    }

    [TestMethod]
    public async Task Composite_PrefersTheHigherPrioritySource_WhateverTheRegistrationOrder()
    {
        // Precedence used to BE registration order, which an edition cannot change: AddCore registers
        // core's sources first, and PsxMarketWatchQuoteSource claims every symbol unconditionally, so a
        // source registered afterwards was never consulted for anything. A push feed was dead on
        // arrival however healthy it was.
        var psx = new StubSource("psx", ("OGDC", 99m)) { Priority = 0 };
        var push = new StubSource("push", ("OGDC", 101m)) { Priority = 500 };

        // Registered LAST, as a plugin's source always is.
        var composite = new CompositeLiveQuoteSource([psx, push],
            NullLogger<CompositeLiveQuoteSource>.Instance);

        var snapshot = await composite.GetQuotesAsync();

        Assert.AreEqual(101m, snapshot.Quotes["OGDC"].Current);
        Assert.AreEqual("push", snapshot.Quotes["OGDC"].Source);
    }

    [TestMethod]
    public async Task Composite_KeepsRegistrationOrderWhenPrioritiesTie()
    {
        // The compatibility half: a source that declares nothing must behave exactly as it did, so
        // equal priorities have to preserve the order they were registered in.
        var first = new StubSource("first", ("OGDC", 100m));
        var second = new StubSource("second", ("OGDC", 99m));

        var composite = new CompositeLiveQuoteSource([first, second],
            NullLogger<CompositeLiveQuoteSource>.Instance);

        var snapshot = await composite.GetQuotesAsync();

        Assert.AreEqual("first", snapshot.Quotes["OGDC"].Source);
    }

    [TestMethod]
    public async Task Composite_SkipsDisabledSources()
    {
        var ahk = new StubSource("ahk", ("OGDC", 100m)) { Enabled = false };
        var psx = new StubSource("psx", ("OGDC", 99m));

        var composite = new CompositeLiveQuoteSource([ahk, psx],
            NullLogger<CompositeLiveQuoteSource>.Instance);

        var snapshot = await composite.GetQuotesAsync();

        Assert.AreEqual(99m, snapshot.Quotes["OGDC"].Current);
    }

    [TestMethod]
    public async Task Composite_SurvivesAThrowingSource()
    {
        var broken = new StubSource("ahk") { Throws = true };
        var psx = new StubSource("psx", ("OGDC", 99m));

        var composite = new CompositeLiveQuoteSource([broken, psx],
            NullLogger<CompositeLiveQuoteSource>.Instance);

        var snapshot = await composite.GetQuotesAsync();

        Assert.AreEqual(1, snapshot.Quotes.Count,
            "One misbehaving source must not cost the snapshot the others could still produce.");
        Assert.IsTrue(snapshot.Warnings.Any(w => w.Contains("ahk")),
            "The failure has to be reported, not swallowed.");
    }

    // ── Order side vocabulary ─────────────────────────────────────────────────

    [TestMethod]
    public void SellSide_NormalizesToThePortalsSpelling()
    {
        // The portal says "SEL". Sending "SELL" matches nothing and reads as "no orders", rather
        // than erroring — which is the kind of filter bug that hides a working order.
        Assert.AreEqual("SEL", TradingAgent.Tools.AhkOrderSide.Normalize("SELL"));
        Assert.AreEqual("SEL", TradingAgent.Tools.AhkOrderSide.Normalize("sel"));
        Assert.AreEqual("BUY", TradingAgent.Tools.AhkOrderSide.Normalize("buy"));
        Assert.AreEqual("ALL", TradingAgent.Tools.AhkOrderSide.Normalize(null));
        Assert.AreEqual("ALL", TradingAgent.Tools.AhkOrderSide.Normalize(""));

        Assert.IsTrue(TradingAgent.Tools.AhkOrderSide.Matches("SEL", "SEL"));
        Assert.IsTrue(TradingAgent.Tools.AhkOrderSide.Matches("SEL", "ALL"));
        Assert.IsFalse(TradingAgent.Tools.AhkOrderSide.Matches("BUY", "SEL"));
    }

    // ── Subscription guard ────────────────────────────────────────────────────

    [TestMethod]
    public void BrowserReleasingTheTradingScreen_ForcesAResubscribe()
    {
        // The portal's own site.js re-subscribes Page1 from its (empty) watch table on every page
        // load, so placing an order silently wipes our subscription. Nothing reports it — GetFeed
        // just starts returning [] — so this has to be driven by the browser lifecycle, not by
        // noticing the silence afterwards.
        var guard = new FeedSubscriptionGuard();

        guard.NoteBrowserHoldsScreen();

        Assert.IsTrue(guard.NoteBrowserReleasedScreen(),
            "Releasing the screen must force a re-subscribe; the page load overwrote ours.");
        Assert.IsFalse(guard.NoteBrowserReleasedScreen(),
            "Only the transition re-subscribes — not every subsequent pass.");
    }

    [TestMethod]
    public void SilenceWatchdog_ResubscribesOnlyAfterTheThreshold()
    {
        var guard = new FeedSubscriptionGuard();

        for (var i = 0; i < 9; i++)
        {
            Assert.IsFalse(
                guard.NotePollResult(appliedAnyQuotes: false, portalMarketOpen: true,
                    hasSubscription: true, silentPollThreshold: 10),
                $"Poll {i + 1} is still within tolerance; a thin market goes quiet briefly.");
        }

        Assert.IsTrue(
            guard.NotePollResult(appliedAnyQuotes: false, portalMarketOpen: true,
                hasSubscription: true, silentPollThreshold: 10),
            "A whole subscribed universe silent for the full threshold during an OPEN market is a "
            + "lost subscription, not a quiet market.");
    }

    [TestMethod]
    public void SilenceWatchdog_IgnoresSilenceWhenTheMarketIsShutOrNothingIsSubscribed()
    {
        var guard = new FeedSubscriptionGuard();

        for (var i = 0; i < 50; i++)
        {
            Assert.IsFalse(
                guard.NotePollResult(appliedAnyQuotes: false, portalMarketOpen: false,
                    hasSubscription: true, silentPollThreshold: 10),
                "An empty feed outside market hours is the correct answer, not a fault.");
            Assert.IsFalse(
                guard.NotePollResult(appliedAnyQuotes: false, portalMarketOpen: true,
                    hasSubscription: false, silentPollThreshold: 10),
                "With nothing subscribed there is nothing to re-send.");
        }
    }

    [TestMethod]
    public void SilenceWatchdog_ResetsOnAnyQuote()
    {
        var guard = new FeedSubscriptionGuard();

        for (var i = 0; i < 9; i++)
            guard.NotePollResult(false, true, true, 10);

        guard.NotePollResult(appliedAnyQuotes: true, portalMarketOpen: true,
            hasSubscription: true, silentPollThreshold: 10);
        Assert.AreEqual(0, guard.SilentPolls);

        Assert.IsFalse(guard.NotePollResult(false, true, true, 10),
            "One quote clears the run; the count must start again rather than resume at 9.");
    }

    [TestMethod]
    public void SilenceWatchdog_ThresholdHasAFloor()
    {
        // A misconfigured 0 or 1 would re-subscribe on essentially every quiet poll, hammering the
        // broker with subscription POSTs during any thin patch.
        var guard = new FeedSubscriptionGuard();

        for (var i = 0; i < 4; i++)
            Assert.IsFalse(guard.NotePollResult(false, true, true, silentPollThreshold: 0));

        Assert.IsTrue(guard.NotePollResult(false, true, true, silentPollThreshold: 0),
            "The floor of 5 applies regardless of how low the configured threshold is.");
    }

    [TestMethod]
    public void BrowserRelease_ClearsPendingSilence_SoOneCauseIsNotReportedTwice()
    {
        var guard = new FeedSubscriptionGuard();

        for (var i = 0; i < 9; i++)
            guard.NotePollResult(false, true, true, 10);

        guard.NoteBrowserHoldsScreen();
        Assert.IsTrue(guard.NoteBrowserReleasedScreen());
        Assert.AreEqual(0, guard.SilentPolls,
            "The re-subscribe just triggered explains the silence; leaving the counter primed would "
            + "fire a second one on the next quiet poll.");
    }

    // ── Watchlist sync ────────────────────────────────────────────────────────

    [TestMethod]
    public void RemovingASymbol_EvictsItFromTheBook_RatherThanServingItUntilItAgesOut()
    {
        // The portal simply stops sending an unsubscribed symbol. Without eviction its last quote
        // stays inside the freshness window and keeps being served as live for MaxQuoteAgeSeconds
        // after it stopped being watched.
        var book = new AhkQuoteBook();
        book.Apply([Quote("OGDC", 100m), Quote("PPL", 200m), Quote("HBL", 150m)], Now);

        var evicted = book.RetainOnly("REG", ["OGDC", "PPL"]);

        Assert.AreEqual(1, evicted);
        var snapshot = book.Snapshot("REG", TimeSpan.FromMinutes(10), Now);
        Assert.AreEqual(2, snapshot.Count);
        Assert.IsFalse(snapshot.ContainsKey("HBL"), "An unwatched symbol must stop being served at once.");
        Assert.IsTrue(snapshot.ContainsKey("OGDC"), "Still-watched symbols must survive the eviction.");
    }

    [TestMethod]
    public void RetainOnly_IsCaseInsensitive_AndLeavesOtherBoardsAlone()
    {
        var book = new AhkQuoteBook();
        book.Apply([Quote("OGDC", 100m, market: "REG"), Quote("OGDC", 88m, market: "ODL")], Now);

        Assert.AreEqual(0, book.RetainOnly("REG", ["ogdc"]),
            "Symbol matching must be case-insensitive; the universe is upper-cased but config may not be.");

        book.RetainOnly("REG", []);
        Assert.AreEqual(1, book.Snapshot("ODL", TimeSpan.FromMinutes(10), Now).Count,
            "Managing the subscribed board must not touch quotes held for another board.");
    }

    // ── Page planning ─────────────────────────────────────────────────────────

    [TestMethod]
    public void DuplicatePages_AreCollapsed_SoASlotIsNeverWipedByItsOwnDuplicate()
    {
        // The exact shape .NET's ConfigurationBinder produces: a pre-populated default list plus the
        // same four names from appsettings binds to EIGHT entries. Left alone, index 4 re-sends
        // "Page1" with the empty slice at that offset and unsubscribes what index 0 just subscribed.
        var bound = new[] { "Page1", "Page2", "Page3", "Page4", "Page1", "Page2", "Page3", "Page4" };

        var pages = FeedPagePlanner.NormalizePages(bound);

        CollectionAssert.AreEqual(new[] { "Page1", "Page2", "Page3", "Page4" }, pages.ToArray());
    }

    [TestMethod]
    public void NormalizePages_TrimsBlanksAndIsCaseInsensitive_AndFallsBackWhenEmpty()
    {
        CollectionAssert.AreEqual(new[] { "Page1", "Page2" },
            FeedPagePlanner.NormalizePages(["  Page1 ", "", "   ", "page1", "Page2"]).ToArray());

        CollectionAssert.AreEqual(FeedPagePlanner.DefaultPages.ToArray(),
            FeedPagePlanner.NormalizePages([]).ToArray());
        CollectionAssert.AreEqual(FeedPagePlanner.DefaultPages.ToArray(),
            FeedPagePlanner.NormalizePages(null).ToArray());
    }

    [TestMethod]
    public void Plan_PutsEverySymbolOnExactlyOnePage_AndStillClearsUnusedSlots()
    {
        var symbols = Enumerable.Range(1, 30).Select(i => $"SYM{i}").ToList();

        var (assignments, dropped) = FeedPagePlanner.Plan(symbols, pageSize: 50,
            pages: FeedPagePlanner.DefaultPages);

        Assert.AreEqual(0, dropped.Count);
        Assert.AreEqual(4, assignments.Count, "Empty slots must still be sent, to clear stale symbols.");
        Assert.AreEqual(30, assignments[0].Symbols.Count);
        Assert.AreEqual(0, assignments[1].Symbols.Count);

        var placed = assignments.SelectMany(a => a.Symbols).ToList();
        CollectionAssert.AreEquivalent(symbols, placed);
        Assert.AreEqual(placed.Count, placed.Distinct().Count(), "No symbol may be assigned twice.");
    }

    [TestMethod]
    public void Plan_ReportsOverflowInsteadOfTruncatingSilently()
    {
        var symbols = Enumerable.Range(1, 120).Select(i => $"SYM{i}").ToList();

        var (assignments, dropped) = FeedPagePlanner.Plan(symbols, pageSize: 50,
            pages: ["Page1", "Page2"]);

        Assert.AreEqual(100, assignments.Sum(a => a.Symbols.Count));
        Assert.AreEqual(20, dropped.Count,
            "Dropped symbols must be named so they can be attributed to PSX, not silently missing.");
        Assert.AreEqual("SYM101", dropped[0]);
    }

    // ── Unreadable order book ─────────────────────────────────────────────────

    [TestMethod]
    public void AFailedRead_IsNotAnEmptyBook()
    {
        // The distinction that matters. On 2026-08-18 the broker blocked account access mid-test and
        // GetOutstanding started returning nothing; everything downstream read that as "no orders",
        // and a cancel was reported verified-complete while the order was still live.
        var failed = OrderBookRead.Failed("access blocked");
        var empty = OrderBookRead.Success([]);

        Assert.IsFalse(failed.Ok);
        Assert.IsFalse(failed.IsConfirmedEmpty,
            "An unreadable book must never satisfy 'the account has no orders'.");

        Assert.IsTrue(empty.Ok);
        Assert.IsTrue(empty.IsConfirmedEmpty,
            "A genuine read returning nothing IS a confirmed-empty book.");
    }

    [TestMethod]
    public void FailedRead_CarriesAReasonAndNoOrders()
    {
        var failed = OrderBookRead.Failed("the session may have expired");

        Assert.AreEqual(0, failed.Orders.Count);
        Assert.IsNotNull(failed.Error);
        StringAssert.Contains(failed.Error, "expired");
    }

    // ── Cancel target selection ───────────────────────────────────────────────

    [TestMethod]
    public void AmbiguousSymbol_IsRefused_WithTheCandidatesNamed()
    {
        // The safety property of the whole cancel path. Two working orders on one symbol and the
        // tool must NOT pick one — a surrendered queue position is not recoverable, and the order
        // might be a protective stop.
        var book = new[]
        {
            Order("1001", "MARI", "BUY", 650m, 10),
            Order("1002", "MARI", "BUY", 640m, 5),
        };

        var result = TradingAgent.Tools.CancelTargetResolver.Resolve(book, null, "MARI", "ALL");

        Assert.IsNull(result.Order, "It must refuse rather than guess.");
        StringAssert.Contains(result.Error, "1001");
        StringAssert.Contains(result.Error, "1002");
        StringAssert.Contains(result.Error, "do not choose one");
    }

    [TestMethod]
    public void SideFilter_DisambiguatesASymbolWithBothABuyAndASell()
    {
        var book = new[]
        {
            Order("1001", "MARI", "BUY", 650m, 10),
            Order("1002", "MARI", "SEL", 710m, 10),
        };

        var buy = TradingAgent.Tools.CancelTargetResolver.Resolve(book, null, "MARI", "BUY");
        var sell = TradingAgent.Tools.CancelTargetResolver.Resolve(book, null, "MARI", "SEL");

        Assert.AreEqual("1001", buy.Order?.OrderNo);
        Assert.AreEqual("1002", sell.Order?.OrderNo, "The portal spells it SEL, and the filter must match that.");
    }

    [TestMethod]
    public void UnknownOrderNumber_IsRefused_AndListsWhatActuallyExists()
    {
        var book = new[] { Order("1001", "MARI", "BUY", 650m, 10) };

        var result = TradingAgent.Tools.CancelTargetResolver.Resolve(book, "9999", "", "ALL");

        Assert.IsNull(result.Order);
        StringAssert.Contains(result.Error, "1001",
            "The usual cause is that the order already filled; the caller needs the current truth.");
    }

    [TestMethod]
    public void EmptyBook_SaysThereIsNothingToCancel()
    {
        var result = TradingAgent.Tools.CancelTargetResolver.Resolve([], null, "MARI", "ALL");

        Assert.IsNull(result.Order);
        StringAssert.Contains(result.Error, "no working orders");
    }

    [TestMethod]
    public void ExactOrderNumber_WinsEvenWhenTheSymbolHasSeveralOrders()
    {
        var book = new[]
        {
            Order("1001", "MARI", "BUY", 650m, 10),
            Order("1002", "MARI", "BUY", 640m, 5),
        };

        var result = TradingAgent.Tools.CancelTargetResolver.Resolve(book, "1002", "MARI", "ALL");

        Assert.AreEqual("1002", result.Order?.OrderNo);
        Assert.IsNull(result.Error);
    }

    private static AhkOutstandingOrder Order(
        string no, string scrip, string type, decimal price, long remaining) =>
        new() { OrderNo = no, Scrip = scrip, Type = type, Price = price, Remaining = remaining, Market = "REG" };

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static AhkFeedQuote Quote(string symbol, decimal last, string market = "REG") =>
        new() { Mkt = market, Symbol = symbol, LastPrice = last };

    private sealed class StubSource : ILiveQuoteSource
    {
        private readonly (string Symbol, decimal Price)[] _quotes;

        public StubSource(string name, params (string Symbol, decimal Price)[] quotes)
        {
            Name = name;
            _quotes = quotes;
        }

        public string Name { get; }
        public int Priority { get; init; }
        public bool Enabled { get; init; } = true;
        public bool Throws { get; init; }
        public bool IsEnabled => Enabled;

        public Task<LiveQuoteSnapshot> GetQuotesAsync(CancellationToken ct = default)
        {
            if (Throws) throw new InvalidOperationException("stub failure");

            var quotes = _quotes.ToDictionary(
                q => q.Symbol,
                q => new PsxLiveQuote { Symbol = q.Symbol, Current = q.Price, Source = Name },
                StringComparer.OrdinalIgnoreCase);

            return Task.FromResult(new LiveQuoteSnapshot { Quotes = quotes, Source = Name });
        }
    }
}
