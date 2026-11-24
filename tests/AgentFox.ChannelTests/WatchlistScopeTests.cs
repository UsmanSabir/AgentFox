using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Persistence;
using TradingAgent.Watchlist;

namespace AgentFox.ChannelTests;

/// <summary>
/// Watchlist mode narrows alerts and armed orders to the watchlist itself (owner, 2026-11-24), and a new
/// user's seeded stocks start manual-only. See <see cref="WatchlistScope"/>.
/// </summary>
[TestClass]
public sealed class WatchlistScopeTests
{
    private static WatchlistSnapshot Snapshot(params string[] symbols) => new(
        symbols.Select(s => new WatchlistEntry(s, DateTime.UtcNow, "user", 0, false, true, null)).ToList(), null, null);

    private static ArmedOrder Order(string symbol, string? protectiveStopId = null) => new()
    {
        ArmedId = "a-" + symbol,
        Symbol = symbol,
        TriggerKind = ArmedTriggerKind.PriceBelow,
        Action = "SELL",
        Quantity = 10,
        OrderType = "LIMIT",
        ProtectiveStopId = protectiveStopId
    };

    [TestMethod]
    public void WatchlistMode_StockOffTheWatchlist_RaisesNoAlertAndFiresNoOrder()
    {
        var watched = WatchlistScope.Watched(TradingExecutionUniverseSource.Watchlist, Snapshot("OGDC"));

        Assert.IsTrue(WatchlistScope.MayAlert("OGDC", watched));
        Assert.IsFalse(WatchlistScope.MayAlert("SELECT", watched),
            "A stock the user never watched (or removed) must not alert.");
        Assert.IsTrue(WatchlistScope.MayFire(Order("OGDC"), watched));
        Assert.IsFalse(WatchlistScope.MayFire(Order("SELECT"), watched));
    }

    [TestMethod]
    public void WatchlistMode_ProtectiveBackstopStillFiresOffTheWatchlist()
    {
        var watched = WatchlistScope.Watched(TradingExecutionUniverseSource.Watchlist, Snapshot("OGDC"));

        Assert.IsTrue(WatchlistScope.MayFire(Order("SELECT", protectiveStopId: "stop-1"), watched),
            "A held position's backstop is its safety net, not an order the user armed.");
    }

    [TestMethod]
    public void WatchlistMode_EmptyWatchlistHoldsEverything()
    {
        var watched = WatchlistScope.Watched(TradingExecutionUniverseSource.Watchlist, Snapshot());

        Assert.IsFalse(WatchlistScope.MayAlert("OGDC", watched));
        Assert.IsFalse(WatchlistScope.MayFire(Order("OGDC"), watched));
    }

    [TestMethod]
    public void AllowedSymbolsMode_NarrowsNothing()
    {
        var watched = WatchlistScope.Watched(TradingExecutionUniverseSource.AllowedSymbols, Snapshot("OGDC"));

        Assert.IsNull(watched);
        Assert.IsTrue(WatchlistScope.MayAlert("SELECT", watched));
        Assert.IsTrue(WatchlistScope.MayFire(Order("SELECT"), watched));
    }

    [TestMethod]
    public async Task SeededAndResetStocksStartManualOnly()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"agentfox-scope-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            var options = Microsoft.Extensions.Options.Options.Create(new TradingAgentOptions
            {
                DatabasePath = Path.Combine(temp, "trading.db"),
                AllowedSymbols = ["OGDC", "LUCK"],
                ExecutionUniverseSource = TradingExecutionUniverseSource.Watchlist
            });
            var repository = new SqliteTradingRepository(
                options, new ConfigurationBuilder().Build(), NullLogger<SqliteTradingRepository>.Instance);
            var universe = new MonitoredUniverse(options, repository, NullLogger<MonitoredUniverse>.Instance);

            await universe.SeedIfNeededAsync();
            var seeded = (await repository.GetWatchlistAsync()).Entries;
            Assert.AreEqual(2, seeded.Count);
            Assert.IsTrue(seeded.All(e => !e.AutoTradeEnabled), "A new user has no automation on any stock.");
            Assert.IsTrue(seeded.All(e => e.AlertsEnabled));
            CollectionAssert.AreEquivalent(new[] { "OGDC", "LUCK" }, (await universe.ManualOnlyAsync()).ToArray());

            await repository.ResetWatchlistAsync(["OGDC", "LUCK"], MonitoredUniverse.SeedHash(["OGDC", "LUCK"]));
            Assert.IsTrue((await repository.GetWatchlistAsync()).Entries.All(e => !e.AutoTradeEnabled));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(temp, recursive: true); } catch { /* temp dir */ }
        }
    }
}
