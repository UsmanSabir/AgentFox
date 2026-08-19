using TradingAgent.AhlAnalytics;

namespace AgentFox.ChannelTests;

/// <summary>
/// The market-mover screens derived from the AHL analytics snapshot.
///
/// <para>
/// The sorts are trivial; the filter in front of them is not, and that is what these cover. The
/// snapshot carries EVERY listed instrument, including ones dormant for months, each still holding
/// the percent change from whenever it last traded. So a "today's biggest movers" list that ranks
/// without comparing each row's own tick date against the market's last-update date fills with dead
/// symbols showing large stale moves — which looks entirely plausible and is entirely wrong. That was
/// observed live: the rights line 786R sat in a real snapshot showing −6.44% from a tick seven months
/// old, and would have topped a losers screen.
/// </para>
///
/// <para>
/// The other cases guard the derived metrics that are easy to get subtly wrong: a gap measured from
/// the wrong close, an unusual-volume ratio that rewards a symbol which barely trades, and the
/// percent/fraction scaling boundary between the wire format and everything downstream.
/// </para>
/// </summary>
[TestClass]
public sealed class AhlMoversTests
{
    private const string Session = "2026-08-19";

    private static AhlSnapshotData Snapshot(params (string Symbol, AhlEquity Equity)[] equities) =>
        new()
        {
            MarketState = "OPN",
            LastUpdate = $"{Session} 15:50:00",
            Equities = equities.ToDictionary(e => e.Symbol, e => e.Equity)
        };

    /// <summary>A symbol that traded in the session under test.</summary>
    private static AhlEquity Fresh(
        decimal close = 100m,
        decimal changeFraction = 0.01m,
        long volume = 100_000,
        decimal? avg10 = 50_000m,
        decimal? previousClose = null,
        decimal? open = null,
        decimal? upperCap = null,
        decimal? lowerLock = null,
        string? sector = "0804",
        List<string>? indices = null,
        string? tickDate = null) => new()
        {
            Name = "Test Co",
            SectorCode = sector,
            LastTickAt = $"{tickDate ?? Session} 15:45:00",
            Close = close,
            Open = open ?? close,
            ChangeFraction = changeFraction,
            Volume = volume,
            AvgVolume10Day = avg10,
            PreviousClose = previousClose ?? close,
            UpperCap = upperCap,
            LowerLock = lowerLock,
            ListedIn = indices ?? ["KSE100"]
        };

    // ── the freshness filter ──────────────────────────────────────────────────

    [TestMethod]
    public void StaleSymbols_AreExcluded_EvenWhenTheirChangeIsLargest()
    {
        // The dormant symbol has by far the biggest move; only its tick date betrays it.
        var snapshot = Snapshot(
            ("LIVE", Fresh(changeFraction: 0.03m)),
            ("DEAD", Fresh(changeFraction: 0.40m, tickDate: "2026-01-02")));

        var gainers = AhlMovers.Run(snapshot, AhlMovers.Screen.Gainers, limit: 10);

        Assert.AreEqual(1, gainers.Count, "the dormant symbol must not be ranked");
        Assert.AreEqual("LIVE", gainers[0].Symbol);
    }

    [TestMethod]
    public void StaleSymbols_AreExcludedFromLosersToo()
    {
        // Guards the direction the live capture actually exhibited: 786R's stale change was negative.
        var snapshot = Snapshot(
            ("LIVE", Fresh(changeFraction: -0.02m)),
            ("DEAD", Fresh(changeFraction: -0.0644m, tickDate: "2026-01-02")));

        var losers = AhlMovers.Run(snapshot, AhlMovers.Screen.Losers);

        Assert.AreEqual(1, losers.Count);
        Assert.AreEqual("LIVE", losers[0].Symbol);
    }

    [TestMethod]
    public void MissingLastUpdate_YieldsNothingRatherThanEverything()
    {
        // Without a reference date, fresh cannot be told from stale. Returning the whole universe
        // would present months-old changes as today's, so the safe answer is none.
        var snapshot = Snapshot(("LIVE", Fresh()));
        snapshot.LastUpdate = null;

        Assert.AreEqual(0, AhlMovers.Run(snapshot, AhlMovers.Screen.Gainers).Count);
        Assert.AreEqual(0, AhlMovers.MarketBreadth(snapshot)!.TradedToday);
    }

    // ── scaling ───────────────────────────────────────────────────────────────

    [TestMethod]
    public void ChangeFraction_IsConvertedToPercentExactlyOnce()
    {
        // `pch` is a fraction on the wire (-0.0048 = -0.48%). Multiplying twice, or not at all, both
        // produce numbers that look like plausible percentages.
        var snapshot = Snapshot(("A", Fresh(changeFraction: 0.0468m)));

        var row = AhlMovers.Run(snapshot, AhlMovers.Screen.Gainers)[0];

        Assert.AreEqual(4.68m, row.ChangePercent);
    }

    // ── derived metrics ───────────────────────────────────────────────────────

    [TestMethod]
    public void UnusualVolume_RanksByRatioAndIgnoresBarelyTradedNames()
    {
        var snapshot = Snapshot(
            // 3x its average — a genuine spike on a liquid name.
            ("SPIKE", Fresh(volume: 300_000, avg10: 100_000m)),
            // Huge ratio but on an average of ~zero: one lot in a name that never trades. Excluding
            // these is the difference between a usable screen and a list of illiquid noise.
            ("THIN", Fresh(volume: 500, avg10: 0m)),
            // Below its average — not unusual at all.
            ("QUIET", Fresh(volume: 50_000, avg10: 100_000m)));

        var rows = AhlMovers.Run(snapshot, AhlMovers.Screen.UnusualVolume);

        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual("SPIKE", rows[0].Symbol);
        Assert.AreEqual(3m, rows[0].VolumeVsAvg10Day);
    }

    [TestMethod]
    public void Gap_IsMeasuredFromThePreviousClose_NotTodaysClose()
    {
        // Opened at 110 against a previous close of 100 — a +10% gap. Using today's close (105)
        // as the reference would report +4.76%, which is the move, not the gap.
        var snapshot = Snapshot(("A", Fresh(close: 105m, open: 110m, previousClose: 100m)));

        var row = AhlMovers.Run(snapshot, AhlMovers.Screen.GapUp)[0];

        Assert.AreEqual(10m, row.GapPercent);
    }

    [TestMethod]
    public void CircuitCaps_AreFlaggedWhenThereIsNoHeadroomLeft()
    {
        // A symbol pinned at its upper cap cannot be bought any higher — the order is refused — so
        // this must read as "at cap" rather than as a fraction of a percent of room.
        var snapshot = Snapshot(
            ("PINNED", Fresh(close: 110m, upperCap: 110m, lowerLock: 90m)),
            ("ROOMY",  Fresh(close: 100m, upperCap: 110m, lowerLock: 90m)));

        var rows = AhlMovers.Run(snapshot, AhlMovers.Screen.NearUpperCap)
                            .ToDictionary(r => r.Symbol);

        Assert.IsTrue(rows["PINNED"].AtUpperCap);
        Assert.IsFalse(rows["ROOMY"].AtUpperCap);
        Assert.AreEqual(0m, rows["PINNED"].DistanceToUpperCapPercent);
    }

    [TestMethod]
    public void MostValuable_RanksByTurnoverNotShareCount()
    {
        // The distinction that matters on PSX: a penny name can lead on share volume while moving a
        // fraction of the money a large-cap does.
        var snapshot = Snapshot(
            ("PENNY", Fresh(close: 1m, volume: 10_000_000)),      // Rs 10mn
            ("LARGE", Fresh(close: 500m, volume: 1_000_000)));    // Rs 500mn

        var byValue = AhlMovers.Run(snapshot, AhlMovers.Screen.MostValuable);
        var byVolume = AhlMovers.Run(snapshot, AhlMovers.Screen.MostActive);

        Assert.AreEqual("LARGE", byValue[0].Symbol);
        Assert.AreEqual("PENNY", byVolume[0].Symbol, "share-count ranking should still favour the penny name");
    }

    // ── filters ───────────────────────────────────────────────────────────────

    [TestMethod]
    public void IndexFilter_RestrictsToMembers()
    {
        var snapshot = Snapshot(
            ("IN",  Fresh(changeFraction: 0.01m, indices: ["KSE100", "ALLSHR"])),
            ("OUT", Fresh(changeFraction: 0.09m, indices: ["ALLSHR"])));

        var rows = AhlMovers.Run(snapshot, AhlMovers.Screen.Gainers,
            filter: new AhlMovers.Filter(Index: "KSE100"));

        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual("IN", rows[0].Symbol);
    }

    [TestMethod]
    public void TurnoverFloor_ExcludesIlliquidNames()
    {
        var snapshot = Snapshot(
            ("BIG",   Fresh(close: 100m, volume: 1_000_000)), // Rs 100mn
            ("SMALL", Fresh(close: 10m, volume: 1_000)));     // Rs 10k

        var rows = AhlMovers.Run(snapshot, AhlMovers.Screen.Gainers,
            filter: new AhlMovers.Filter(MinTurnoverPkr: 1_000_000m));

        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual("BIG", rows[0].Symbol);
    }

    // ── aggregates ────────────────────────────────────────────────────────────

    [TestMethod]
    public void Breadth_CountsOnlySymbolsThatTradedThisSession()
    {
        var snapshot = Snapshot(
            ("UP1",  Fresh(changeFraction: 0.02m)),
            ("UP2",  Fresh(changeFraction: 0.01m)),
            ("DOWN", Fresh(changeFraction: -0.01m)),
            ("FLAT", Fresh(changeFraction: 0m)),
            ("DEAD", Fresh(changeFraction: 0.30m, tickDate: "2026-01-02")));

        var breadth = AhlMovers.MarketBreadth(snapshot)!;

        Assert.AreEqual(4, breadth.TradedToday);
        Assert.AreEqual(5, breadth.TotalListed, "the dormant symbol is still listed, just not traded");
        Assert.AreEqual(2, breadth.Advancing);
        Assert.AreEqual(1, breadth.Declining);
        Assert.AreEqual(1, breadth.Unchanged);
    }

    [TestMethod]
    public void SectorRotation_UsesMedianAndSkipsSingleSymbolSectors()
    {
        var snapshot = Snapshot(
            // Cement: -1%, +2%, +3% → median +2%. A mean would be +1.33%, dragged by the outlier.
            ("C1", Fresh(sector: "0804", changeFraction: -0.01m)),
            ("C2", Fresh(sector: "0804", changeFraction: 0.02m)),
            ("C3", Fresh(sector: "0804", changeFraction: 0.03m)),
            // A lone symbol is not a sector reading, it is that symbol.
            ("S1", Fresh(sector: "0826", changeFraction: 0.10m)));

        var sectors = AhlMovers.SectorRotation(snapshot);

        Assert.AreEqual(1, sectors.Count);
        Assert.AreEqual("0804", sectors[0].SectorCode);
        Assert.AreEqual("Cement", sectors[0].SectorName);
        Assert.AreEqual(2m, sectors[0].MedianChangePercent);
        Assert.AreEqual(2, sectors[0].Advancing);
        Assert.AreEqual(1, sectors[0].Declining);
    }

    // ── argument parsing ──────────────────────────────────────────────────────

    [TestMethod]
    public void ScreenNames_AllParse()
    {
        // The advertised list and the parser must not drift: a name in the tool schema that the parser
        // rejects is a tool that fails on its own documented input.
        foreach (var name in AhlMovers.ScreenNames)
            Assert.IsNotNull(AhlMovers.ParseScreen(name), $"'{name}' is advertised but does not parse");

        Assert.IsNull(AhlMovers.ParseScreen("nonsense"));
        Assert.AreEqual(AhlMovers.Screen.MostActive, AhlMovers.ParseScreen("LEADERS"),
            "the portal's own label for most-active should be accepted");
    }

    [TestMethod]
    public void EmptySnapshot_IsHandledWithoutThrowing()
    {
        Assert.AreEqual(0, AhlMovers.Run(null, AhlMovers.Screen.Gainers).Count);
        Assert.AreEqual(0, AhlMovers.Run(new AhlSnapshotData(), AhlMovers.Screen.Gainers).Count);
        Assert.IsNull(AhlMovers.MarketBreadth(null));
        Assert.AreEqual(0, AhlMovers.SectorRotation(null).Count);
    }
}
