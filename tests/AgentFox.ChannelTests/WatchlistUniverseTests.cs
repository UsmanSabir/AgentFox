using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Market;
using TradingAgent.Persistence;
using TradingAgent.Watchlist;

namespace AgentFox.ChannelTests;

/// <summary>
/// Covers the split between what may be WATCHED and what may be TRADED.
///
/// The invariant under test throughout: no watchlist operation may ever widen
/// <see cref="MonitoredUniverse.ForExecution"/>. That list is what the risk engine enforces, so if
/// editing a watchlist could grow it, the UI would have become an order-permission editor.
/// </summary>
[TestClass]
public sealed class WatchlistUniverseTests
{
    [TestMethod]
    public async Task Watchlist_SeedsFromAllowedSymbolsOnce()
    {
        using var env = TestEnv.Create(["OGDC", "LUCK"]);

        await env.Universe.SeedIfNeededAsync();
        var seeded = await env.Repository.GetWatchlistAsync();

        CollectionAssert.AreEquivalent(
            new[] { "OGDC", "LUCK" }, seeded.Entries.Select(e => e.Symbol).ToArray());
        Assert.IsTrue(seeded.Entries.All(e => e.Source == "seed"));
        Assert.IsNotNull(seeded.SeededUtc);

        // A user removes a seeded symbol; seeding must not put it back, or a deliberate removal would
        // silently undo itself on the next call.
        Assert.IsTrue(await env.Repository.RemoveWatchlistSymbolAsync("LUCK"));
        await env.Universe.SeedIfNeededAsync();

        var after = await env.Repository.GetWatchlistAsync();
        CollectionAssert.AreEqual(new[] { "OGDC" }, after.Entries.Select(e => e.Symbol).ToArray());
    }

    [TestMethod]
    public async Task AddingToWatchlist_WidensMonitoringButNeverExecution()
    {
        using var env = TestEnv.Create(["OGDC"]);

        Assert.IsTrue(await env.Repository.AddWatchlistSymbolAsync("HBL", "user"));
        env.Universe.Invalidate();

        var monitoring = await env.Universe.ForMonitoringAsync();
        CollectionAssert.Contains(monitoring.ToArray(), "HBL", "A watched symbol must be monitored.");

        var execution = env.Universe.ForExecution();
        CollectionAssert.DoesNotContain(execution.ToArray(), "HBL",
            "Watchlist edits must never widen the tradable universe.");
        CollectionAssert.AreEqual(new[] { "OGDC" }, execution.ToArray());

        Assert.IsFalse(env.Universe.IsTradable("HBL"));
        Assert.IsTrue(env.Universe.IsTradable("ogdc"), "Tradability is case-insensitive.");
    }

    [TestMethod]
    public async Task ArchiveUniverse_IncludesWatchlistUnlessDisabled()
    {
        using var env = TestEnv.Create(["OGDC"]);
        await env.Repository.AddWatchlistSymbolAsync("HBL", "user");
        env.Universe.Invalidate();

        // On by default: a watched symbol needs the same deep daily history as a tradable one, or its
        // weekly levels cannot be computed at all.
        CollectionAssert.Contains((await env.Universe.ForArchiveAsync()).ToArray(), "HBL");

        env.Options.Value.Watchlist.ArchiveWatchlistSymbols = false;
        env.Universe.Invalidate();
        CollectionAssert.DoesNotContain((await env.Universe.ForArchiveAsync()).ToArray(), "HBL");
    }

    [TestMethod]
    public async Task Reset_RestoresConfiguredListAndDiscardsUserEdits()
    {
        using var env = TestEnv.Create(["OGDC", "LUCK"]);
        await env.Universe.SeedIfNeededAsync();

        await env.Repository.AddWatchlistSymbolAsync("HBL", "user");
        await env.Repository.RemoveWatchlistSymbolAsync("LUCK");

        var seed = env.Universe.ForExecution();
        var count = await env.Repository.ResetWatchlistAsync(seed, MonitoredUniverse.SeedHash(seed));

        Assert.AreEqual(2, count);
        var after = await env.Repository.GetWatchlistAsync();
        CollectionAssert.AreEquivalent(
            new[] { "OGDC", "LUCK" }, after.Entries.Select(e => e.Symbol).ToArray());
        Assert.AreEqual(MonitoredUniverse.SeedHash(seed), after.SeedHash);
    }

    [TestMethod]
    public async Task SeedHash_DetectsChangedAllowedSymbolsWithoutResaeding()
    {
        using var env = TestEnv.Create(["OGDC", "LUCK"]);
        await env.Universe.SeedIfNeededAsync();
        var seeded = await env.Repository.GetWatchlistAsync();
        Assert.AreEqual(env.Universe.CurrentSeedHash(), seeded.SeedHash);

        // Configuration changes underneath us. The watchlist must NOT follow it — that would discard
        // the user's edits — but the hash must now differ so the UI can offer a reset.
        env.Options.Value.AllowedSymbols = ["OGDC", "LUCK", "HBL"];
        env.Universe.Invalidate();

        Assert.AreNotEqual(env.Universe.CurrentSeedHash(), seeded.SeedHash);
        await env.Universe.SeedIfNeededAsync();
        var after = await env.Repository.GetWatchlistAsync();
        Assert.AreEqual(2, after.Entries.Count, "Re-seeding must not happen automatically.");
    }

    [TestMethod]
    public void SeedHash_IsOrderInsensitive() =>
        Assert.AreEqual(
            MonitoredUniverse.SeedHash(["OGDC", "LUCK"]),
            MonitoredUniverse.SeedHash([" luck ", "ogdc"]),
            "Hashing must ignore order and casing, or every restart would look like a config change.");

    [TestMethod]
    public async Task Add_IsIdempotentAndUpdatePreservesUnspecifiedFields()
    {
        using var env = TestEnv.Create([]);

        Assert.IsTrue(await env.Repository.AddWatchlistSymbolAsync("OGDC", "user"));
        Assert.IsFalse(await env.Repository.AddWatchlistSymbolAsync("OGDC", "user"),
            "A duplicate add must report false rather than creating a second row.");

        await env.Repository.UpdateWatchlistSymbolAsync("OGDC", alertsEnabled: false, notes: "watching");
        await env.Repository.UpdateWatchlistSymbolAsync("OGDC", alertsEnabled: null, notes: null, pinned: true);
        // Patching one field must not blank the other.
        await env.Repository.UpdateWatchlistSymbolAsync("OGDC", alertsEnabled: null, notes: "still watching");

        var entry = (await env.Repository.GetWatchlistAsync()).Entries.Single();
        Assert.IsFalse(entry.AlertsEnabled);
        Assert.IsTrue(entry.Pinned);
        Assert.AreEqual("still watching", entry.Notes);

        Assert.IsFalse(await env.Repository.UpdateWatchlistSymbolAsync("NOPE", true, null));
        Assert.IsFalse(await env.Repository.RemoveWatchlistSymbolAsync("NOPE"));
    }

    [TestMethod]
    public async Task ReorderAndPin_AreDurableAndPinnedSymbolsStayFirst()
    {
        using var env = TestEnv.Create(["OGDC", "LUCK", "HBL"]);
        await env.Universe.SeedIfNeededAsync();

        Assert.IsTrue(await env.Repository.ReorderWatchlistAsync(["HBL", "OGDC", "LUCK"]));
        Assert.IsTrue(await env.Repository.UpdateWatchlistSymbolAsync(
            "LUCK", alertsEnabled: null, notes: null, pinned: true));

        var pinned = await env.Repository.GetWatchlistAsync();
        CollectionAssert.AreEqual(
            new[] { "LUCK", "HBL", "OGDC" }, pinned.Entries.Select(e => e.Symbol).ToArray());

        Assert.IsTrue(await env.Repository.UpdateWatchlistSymbolAsync(
            "LUCK", alertsEnabled: null, notes: null, pinned: false));
        var unpinned = await env.Repository.GetWatchlistAsync();
        CollectionAssert.AreEqual(
            new[] { "HBL", "OGDC", "LUCK" }, unpinned.Entries.Select(e => e.Symbol).ToArray());

        Assert.IsFalse(await env.Repository.ReorderWatchlistAsync(["HBL", "OGDC"]),
            "A stale partial order must not silently drop or reshuffle unseen symbols.");
    }

    [TestMethod]
    public async Task MonitoringFallsBackToAllowedSymbolsWhenTheDatabaseFails()
    {
        var options = Options.Create(new TradingAgentOptions { AllowedSymbols = ["OGDC", "LUCK"] });
        var universe = new MonitoredUniverse(
            options, new ThrowingRepository(), NullLogger<MonitoredUniverse>.Instance);

        // Losing the watchlist must degrade monitoring to the configured list, not stop it.
        CollectionAssert.AreEquivalent(
            new[] { "OGDC", "LUCK" }, (await universe.ForMonitoringAsync()).ToArray());
    }

    // ── Settled-session guard ────────────────────────────────────────────────

    [TestMethod]
    [DataRow("2026-08-12T06:00:00Z", "2026-08-11")] // Wed 11:00 PKT — market open, today unsettled
    [DataRow("2026-08-12T11:00:00Z", "2026-08-11")] // Wed 16:00 PKT — closed but before settle time
    [DataRow("2026-08-12T13:00:00Z", "2026-08-12")] // Wed 18:00 PKT — past 17:30, today is final
    public async Task Backfill_OnlyTreatsSettledSessionsAsArchivable(string utc, string expected)
    {
        using var env = TestEnv.Create(["OGDC"]);
        var runner = env.CreateBackfillRunner();

        var settled = runner.LastSettledSession(DateTime.Parse(utc).ToUniversalTime());

        Assert.AreEqual(
            DateOnly.Parse(expected), settled,
            "Archiving a session before it settles stores a partial bar that the coverage marker then "
            + "prevents from ever being corrected.");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task ClearDailyCoverageAfter_RepairsPrematurelyRecordedSessions()
    {
        using var env = TestEnv.Create(["OGDC"]);
        var settled = new DateOnly(2026, 8, 11);
        var unsettled = new DateOnly(2026, 8, 12);

        await env.Repository.SaveNonTradingDayAsync(settled);
        // Simulates the damage: the in-progress session was recorded as covered (as an empty day, i.e.
        // indistinguishable from a holiday), which without repair is a permanent hole.
        await env.Repository.SaveNonTradingDayAsync(unsettled);

        string[] universe = ["OGDC"];
        var covered = await env.Repository.GetCoveredDailyDatesAsync(settled, unsettled, universe);
        Assert.AreEqual(2, covered.Count);

        Assert.AreEqual(1, await env.Repository.ClearDailyCoverageAfterAsync(settled));

        covered = await env.Repository.GetCoveredDailyDatesAsync(settled, unsettled, universe);
        CollectionAssert.AreEqual(new[] { settled }, covered.ToArray(),
            "Only the unsettled date's marker may be dropped, so the settled history is untouched.");
    }

    // ── Symbol-aware coverage ────────────────────────────────────────────────

    [TestMethod]
    public async Task Coverage_IsRecordedPerSymbolSoALaterSymbolIsNotSilentlySkipped()
    {
        using var env = TestEnv.Create(["OGDC"]);
        var session = new DateOnly(2026, 8, 11);

        // The archive universe when the date was fetched. GHNI joined afterwards.
        await env.Repository.SaveDailySessionAsync(session, [Candle("OGDC", session)], ["OGDC"]);

        Assert.AreEqual(
            1, (await env.Repository.GetCoveredDailyDatesAsync(session, session, ["OGDC"])).Count,
            "The symbol the fetch was filtered to is covered.");

        Assert.AreEqual(
            0, (await env.Repository.GetCoveredDailyDatesAsync(session, session, ["OGDC", "GHNI"])).Count,
            "A date is only skippable once EVERY requested symbol has been asked for on it. Counting it "
            + "covered for a symbol added later is what left that symbol permanently starved of history.");

        // A pass targeting GHNI refetches the date, which stores the whole market for it.
        await env.Repository.SaveDailySessionAsync(
            session, [Candle("OGDC", session), Candle("GHNI", session)], ["OGDC", "GHNI"]);

        Assert.AreEqual(
            1, (await env.Repository.GetCoveredDailyDatesAsync(session, session, ["OGDC", "GHNI"])).Count,
            "Once both symbols have been requested the date is complete for the pair.");
    }

    [TestMethod]
    public async Task NonTradingDay_CountsAsCoveredForSymbolsAddedLater()
    {
        using var env = TestEnv.Create(["OGDC"]);
        var holiday = new DateOnly(2026, 8, 14);

        await env.Repository.SaveNonTradingDayAsync(holiday);

        // A day the market was shut has nothing to fetch for anyone, ever. Requiring a per-symbol row
        // would make every holiday reappear as missing the moment a symbol is added.
        Assert.AreEqual(
            1,
            (await env.Repository.GetCoveredDailyDatesAsync(holiday, holiday, ["OGDC", "GHNI"])).Count,
            "A non-trading day is covered for every symbol, including ones added later.");

        var counts = await env.Repository.GetCoveredDailyDateCountsAsync(
            holiday, holiday, ["OGDC", "GHNI"]);
        Assert.AreEqual(1, counts["GHNI"],
            "The market-wide closure counts toward a symbol that has no rows of its own.");
    }

    [TestMethod]
    public async Task CoveredDateCounts_ReportEachSymbolsOwnShortfall()
    {
        using var env = TestEnv.Create(["OGDC"]);
        var first = new DateOnly(2026, 8, 10);
        var second = new DateOnly(2026, 8, 11);

        await env.Repository.SaveDailySessionAsync(first, [Candle("OGDC", first)], ["OGDC"]);
        await env.Repository.SaveDailySessionAsync(
            second, [Candle("OGDC", second), Candle("GHNI", second)], ["OGDC", "GHNI"]);

        var counts = await env.Repository.GetCoveredDailyDateCountsAsync(first, second, ["OGDC", "GHNI"]);

        Assert.AreEqual(2, counts["OGDC"]);
        Assert.AreEqual(1, counts["GHNI"],
            "GHNI was only in the universe for the second session, so it is one session short — the "
            + "number a targeted backfill has to fetch.");
    }

    [TestMethod]
    public async Task SaveDailySession_DoesNotClaimCoverageForAnUnsettledBar()
    {
        using var env = TestEnv.Create(["OGDC"]);
        var session = new DateOnly(2026, 8, 11);

        var live = Candle("OGDC", session) with { IsLive = true };
        await env.Repository.SaveDailySessionAsync(session, [live], ["OGDC"]);

        Assert.AreEqual(
            0, (await env.Repository.GetCoveredDailyDatesAsync(session, session, ["OGDC"])).Count,
            "The bar was rejected as unsettled, so claiming the symbol as covered would freeze the gap "
            + "that rejection exists to avoid.");
    }

    [TestMethod]
    public async Task BulkAlertState_ChangesOnlyTheSelectedRows()
    {
        using var env = TestEnv.Create(["OGDC", "LUCK"]);
        var first = await env.Repository.SaveAlertAsync(Alert("OGDC"), new DateOnly(2026, 8, 21));
        var second = await env.Repository.SaveAlertAsync(Alert("LUCK"), new DateOnly(2026, 8, 21));

        Assert.AreEqual(1, await env.Repository.SetAlertsStateAsync(
            [first], "acknowledged", "new"));

        var rows = await env.Repository.GetAlertsAsync(limit: 10);
        Assert.AreEqual("acknowledged", rows.Single(row => row.AlertId == first).State);
        Assert.AreEqual("new", rows.Single(row => row.AlertId == second).State);

        Assert.AreEqual(2, await env.Repository.SetAlertsStateAsync(null, "dismissed"));
        Assert.IsTrue((await env.Repository.GetAlertsAsync(limit: 10))
            .All(row => row.State == "dismissed"));
    }

    [TestMethod]
    public async Task AlertRetention_PhysicallyDeletesDismissedRowsAfterTheCutoff()
    {
        using var env = TestEnv.Create(["OGDC"]);
        var id = await env.Repository.SaveAlertAsync(Alert("OGDC"), new DateOnly(2026, 8, 21));
        await env.Repository.SetAlertStateAsync(id, "dismissed");

        Assert.AreEqual(1, await env.Repository.PruneAlertsAsync(DateTime.UtcNow.AddMinutes(1)));
        Assert.AreEqual(0, (await env.Repository.GetAlertsAsync(limit: 10)).Count,
            "retention is a real DELETE; SQLite can reuse the released page space");
    }

    private static DetectedAlert Alert(string symbol) => new()
    {
        Symbol = symbol,
        Kind = AlertKind.SupportBounce,
        Severity = AlertSeverity.Medium,
        Price = 100m,
        Interval = "1D",
        Summary = $"{symbol} bounced"
    };

    private static TradingAgent.Research.PsxCandle Candle(string symbol, DateOnly date) => new()
    {
        Symbol = symbol,
        Date   = date,
        Open   = 100m,
        High   = 105m,
        Low    = 99m,
        Close  = 104m,
        Volume = 1_000
    };

    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed class TestEnv : IDisposable
    {
        public required IOptions<TradingAgentOptions> Options { get; init; }
        public required SqliteTradingRepository Repository { get; init; }
        public required MonitoredUniverse Universe { get; init; }
        public required string TempPath { get; init; }

        public static TestEnv Create(string[] allowedSymbols)
        {
            var temp = Path.Combine(Path.GetTempPath(), $"agentfox-watchlist-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temp);

            var options = Microsoft.Extensions.Options.Options.Create(new TradingAgentOptions
            {
                DatabasePath = Path.Combine(temp, "trading.db"),
                AllowedSymbols = [.. allowedSymbols]
            });
            var repository = new SqliteTradingRepository(
                options, new ConfigurationBuilder().Build(),
                NullLogger<SqliteTradingRepository>.Instance);

            return new TestEnv
            {
                Options = options,
                Repository = repository,
                Universe = new MonitoredUniverse(
                    options, repository, NullLogger<MonitoredUniverse>.Instance),
                TempPath = temp
            };
        }

        public TradingAgent.Trading.CandleBackfillRunner CreateBackfillRunner() =>
            new(
                new TradingAgent.Research.PsxDataClient(
                    Options, NullLogger<TradingAgent.Research.PsxDataClient>.Instance),
                Repository,
                Universe,
                new PsxMarketCalendar(Options, NullLogger<PsxMarketCalendar>.Instance),
                Options,
                new StubLifetime(),
                NullLogger<TradingAgent.Trading.CandleBackfillRunner>.Instance);

        public void Dispose()
        {
            try { Directory.Delete(TempPath, recursive: true); } catch { /* temp dir */ }
        }
    }

    private sealed class StubLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    /// <summary>Repository whose watchlist reads always fail, for the degradation test.</summary>
    private sealed class ThrowingRepository : ITradingRepository
    {
        public Task RecordBrokerOrdersAsync(
            string executionId,
            IReadOnlyList<TradingAgent.Models.OrderResult> orders,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<int> RecordFillsAsync(
            IReadOnlyList<TradingAgent.Reconciliation.BrokerFill> fills,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<WatchlistSnapshot> GetWatchlistAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("database unavailable");

        public Task<bool> EnsureWatchlistSeededAsync(
            IReadOnlyList<string> seed, string seedHash, CancellationToken ct = default) =>
            throw new InvalidOperationException("database unavailable");

        public Task<string> CreateProposalAsync(string a, string b, string c, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<TradingLedgerStatus> GetStatusAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<TradeProposalRecord>> GetProposalsAsync(int l = 100, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<TradingExecutionRecord>> GetExecutionsAsync(int l = 100, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<TradingEventRecord>> GetEventsAsync(int l = 200, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<ReconciliationRunRecord>> GetReconciliationRunsAsync(int l = 100, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task RecordReconciliationAsync(
            TradingAgent.Reconciliation.BrokerReconciliationSnapshot s, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<TradingAgent.Manager.ExecutionClaim> TryBeginExecutionAsync(
            string a, string b, string c, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task CompleteExecutionAsync(string a, string b, string c, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> ResolveUnknownExecutionAsync(string a, string b, string c, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task AppendEventAsync(string a, string b, string c, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task SaveDailySessionAsync(
            DateOnly d, IReadOnlyList<TradingAgent.Research.PsxCandle> b,
            IReadOnlyCollection<string> r, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task SaveNonTradingDayAsync(DateOnly d, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<TradingAgent.Research.PsxCandle>> GetDailyBarsAsync(
            string s, int m, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlySet<DateOnly>> GetCoveredDailyDatesAsync(
            DateOnly f, DateOnly t, IReadOnlyCollection<string> y, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, int>> GetCoveredDailyDateCountsAsync(
            DateOnly f, DateOnly t, IReadOnlyCollection<string> y, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<int> ClearDailyCoverageAfterAsync(DateOnly t, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<DailyArchiveStatus> GetDailyArchiveStatusAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task SaveIntradayBarsAsync(
            IReadOnlyList<TradingAgent.Research.PsxCandle> b, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<TradingAgent.Research.PsxCandle>> GetIntradayBarsAsync(
            string s, int i, int m, DateTime? before = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> AddWatchlistSymbolAsync(string s, string src, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> RemoveWatchlistSymbolAsync(string s, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> UpdateWatchlistSymbolAsync(
            string s, bool? a, string? n, bool? p = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> ReorderWatchlistAsync(
            IReadOnlyList<string> symbols, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> ResetWatchlistAsync(
            IReadOnlyList<string> seed, string hash, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, int>> GetDailyBarCountsAsync(
            IReadOnlyList<string> symbols, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, SymbolMonitorState>> GetMonitorStatesAsync(
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task SaveMonitorStateAsync(SymbolMonitorState s, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<string> SaveAlertAsync(DetectedAlert a, DateOnly d, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> HasRecentAlertAsync(
            string s, AlertKind k, decimal? l, DateTime since, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<AlertRecord>> GetAlertsAsync(
            string? s = null, string? st = null, int limit = 100, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<AlertRecord?> GetAlertAsync(string alertId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> SetAlertStateAsync(string id, string state, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<int> SetAlertsStateAsync(
            IReadOnlyCollection<string>? ids, string state, string? fromState = null,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, int>> GetOpenAlertCountsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<int> PruneAlertsAsync(DateTime before, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<TradeProposalRecord?> GetProposalAsync(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> TrySetProposalStateAsync(
            string id, string expected, string next, string? reason = null,
            string? executionId = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<TradeProposalRecord>> GetOpenProposalsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<int> PruneProposalsAsync(DateTime before, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<string> SaveArmedOrderAsync(ArmedOrder order, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<ArmedOrder>> GetArmedOrdersAsync(
            bool armedOnly = true, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> TrySetArmedOrderStateAsync(
            string id, string expected, string next, string? reason = null,
            string? executionId = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> TrySetArmedOrderTrailAsync(
            string id, decimal reference, decimal triggerPrice, bool ratchetUp,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> SaveProtectiveStopAsync(
            ProtectiveStop stop, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProtectiveStop>> GetProtectiveStopsAsync(
            bool openOnly = true, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> TrySetProtectiveStopStateAsync(
            string id, string expected, string next, string? reason = null,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> RecordProtectiveStopFillAsync(
            string id, int qty, string reason, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> RecordProtectiveStopPlacementAsync(
            string id, DateOnly session, int qty, string? orderNo, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> RecordProtectiveStopBaselineAsync(
            string id, int baseline, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> SetProtectiveStopBackstopAsync(
            string id, string? backstopArmedId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
