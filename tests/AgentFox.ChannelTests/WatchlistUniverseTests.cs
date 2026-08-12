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
        // Patching one field must not blank the other.
        await env.Repository.UpdateWatchlistSymbolAsync("OGDC", alertsEnabled: null, notes: "still watching");

        var entry = (await env.Repository.GetWatchlistAsync()).Entries.Single();
        Assert.IsFalse(entry.AlertsEnabled);
        Assert.AreEqual("still watching", entry.Notes);

        Assert.IsFalse(await env.Repository.UpdateWatchlistSymbolAsync("NOPE", true, null));
        Assert.IsFalse(await env.Repository.RemoveWatchlistSymbolAsync("NOPE"));
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

        await env.Repository.SaveDailySessionAsync(settled, []);
        // Simulates the damage: the in-progress session was recorded as covered (as an empty day, i.e.
        // indistinguishable from a holiday), which without repair is a permanent hole.
        await env.Repository.SaveDailySessionAsync(unsettled, []);

        var covered = await env.Repository.GetCoveredDailyDatesAsync(settled, unsettled);
        Assert.AreEqual(2, covered.Count);

        Assert.AreEqual(1, await env.Repository.ClearDailyCoverageAfterAsync(settled));

        covered = await env.Repository.GetCoveredDailyDatesAsync(settled, unsettled);
        CollectionAssert.AreEqual(new[] { settled }, covered.ToArray(),
            "Only the unsettled date's marker may be dropped, so the settled history is untouched.");
    }

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
        public Task AppendEventAsync(string a, string b, string c, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task SaveDailySessionAsync(
            DateOnly d, IReadOnlyList<TradingAgent.Research.PsxCandle> b, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<TradingAgent.Research.PsxCandle>> GetDailyBarsAsync(
            string s, int m, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlySet<DateOnly>> GetCoveredDailyDatesAsync(
            DateOnly f, DateOnly t, CancellationToken ct = default) => throw new NotSupportedException();
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
            string s, bool? a, string? n, CancellationToken ct = default) => throw new NotSupportedException();
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
    }
}
