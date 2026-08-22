using AgentFox.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingAgent.Broker;
using TradingAgent.Config;
using TradingAgent.Manager;
using TradingAgent.Market;
using TradingAgent.Models;
using TradingAgent.Persistence;
using TradingAgent.Reconciliation;
using TradingAgent.Risk;
using TradingAgent.Watchlist;

namespace AgentFox.ChannelTests;

/// <summary>
/// Covers the manual-only deny set: symbols the operator trades BY HAND, which no automation may
/// originate an order for.
///
/// <para>
/// The distinction under test throughout is that manual-only asks a different question from
/// <see cref="TradingAgentOptions.AllowedSymbols"/>. That list answers "may this order exist" and
/// gives the same answer to everyone, so it cannot express "only I may trade this" — removing a symbol
/// from it bans the operator too. Manual-only therefore lives at the automation boundary, and these
/// tests pin both halves of that: the symbol stays fully tradable and fully monitored, while an
/// unattended caller is refused.
/// </para>
/// </summary>
[TestClass]
public sealed class ManualOnlySymbolTests
{
    [TestMethod]
    public async Task DenySet_IsConfigUnionWatchlistToggle()
    {
        using var env = Env.Create(allowed: ["OGDC", "LUCK", "HBL"], manualOnly: ["OGDC"]);
        await env.Universe.SeedIfNeededAsync();

        Assert.IsTrue(await env.Repository.UpdateWatchlistSymbolAsync(
            "LUCK", alertsEnabled: null, notes: null, pinned: null, autoTradeEnabled: false));
        env.Universe.Invalidate();

        var deny = await env.Universe.ManualOnlyAsync();
        CollectionAssert.AreEquivalent(new[] { "OGDC", "LUCK" }, deny.ToArray());
        Assert.IsFalse(deny.Contains("HBL"), "An untouched symbol must stay automatable.");
        Assert.IsTrue(await env.Universe.IsManualOnlyAsync("ogdc"), "Matching is case-insensitive.");
    }

    [TestMethod]
    public async Task ManualOnly_NarrowsNothingElse()
    {
        using var env = Env.Create(allowed: ["OGDC"], manualOnly: ["OGDC"]);
        await env.Universe.SeedIfNeededAsync();

        // The whole point: a hand-managed symbol is still fully tradable and fully watched. If any of
        // these changed, "manual-only" would have collapsed back into "removed from AllowedSymbols".
        CollectionAssert.AreEqual(new[] { "OGDC" }, env.Universe.ForExecution().ToArray());
        Assert.IsTrue(env.Universe.IsTradable("OGDC"));
        CollectionAssert.Contains((await env.Universe.ForMonitoringAsync()).ToArray(), "OGDC");
        CollectionAssert.Contains((await env.Universe.ForArchiveAsync()).ToArray(), "OGDC");
    }

    [TestMethod]
    public async Task WatchlistToggle_CannotLiftAConfiguredPin()
    {
        using var env = Env.Create(allowed: ["OGDC"], manualOnly: ["OGDC"]);
        await env.Universe.SeedIfNeededAsync();

        // Switching automation back on is the loosening direction, and configuration is the floor: the
        // stored flag changes, the effective answer does not.
        Assert.IsTrue(await env.Repository.UpdateWatchlistSymbolAsync(
            "OGDC", alertsEnabled: null, notes: null, pinned: null, autoTradeEnabled: true));
        env.Universe.Invalidate();

        var entry = (await env.Repository.GetWatchlistAsync()).Entries.Single();
        Assert.IsTrue(entry.AutoTradeEnabled, "The stored toggle is the user's and is honoured as set.");
        Assert.IsTrue(await env.Universe.IsManualOnlyAsync("OGDC"),
            "Configuration must outrank the API, or the durable floor would be editable over HTTP.");
    }

    [TestMethod]
    public async Task Toggle_DefaultsToAutomationAllowed_AndSurvivesUnrelatedPatches()
    {
        using var env = Env.Create(allowed: ["OGDC"], manualOnly: []);
        await env.Universe.SeedIfNeededAsync();

        // A migrated database and a freshly added symbol must both behave exactly as before the flag
        // existed; an opt-in restriction that arrived switched on would be the same bug reversed.
        Assert.IsTrue((await env.Repository.GetWatchlistAsync()).Entries.Single().AutoTradeEnabled);
        Assert.IsTrue(await env.Repository.AddWatchlistSymbolAsync("HBL", "user"));
        Assert.IsTrue((await env.Repository.GetWatchlistAsync())
            .Entries.Single(e => e.Symbol == "HBL").AutoTradeEnabled);

        await env.Repository.UpdateWatchlistSymbolAsync(
            "OGDC", alertsEnabled: null, notes: null, pinned: null, autoTradeEnabled: false);
        // Patching an unrelated field must not resurrect automation for the symbol.
        await env.Repository.UpdateWatchlistSymbolAsync("OGDC", alertsEnabled: false, notes: "mine");

        var entry = (await env.Repository.GetWatchlistAsync()).Entries.Single(e => e.Symbol == "OGDC");
        Assert.IsFalse(entry.AutoTradeEnabled);
        Assert.AreEqual("mine", entry.Notes);
    }

    [TestMethod]
    public async Task ApprovalGate_DeniesManualOnly_EvenInBoundedAuto()
    {
        using var env = Env.Create(allowed: ["OGDC"], manualOnly: ["OGDC"], mode: "BoundedAuto");
        await env.Universe.ForMonitoringAsync();   // warms the synchronous snapshot

        var gate = env.CreateApprovalGate();
        var decision = gate.Decide(Groups("OGDC"), "armed-order:test", new ApprovalContext(null, "test"));

        // BoundedAuto normally short-circuits to NotRequired — "the mode itself authorises this" — which
        // is exactly why the manual-only check has to run BEFORE it rather than after.
        Assert.IsFalse(decision.MayProceed, decision.Reason);
        StringAssert.Contains(decision.Reason, "manual-only");
        StringAssert.Contains(decision.Reason, "OGDC");

        var allowed = gate.Decide(Groups("HBL"), "armed-order:test", new ApprovalContext(null, "test"));
        Assert.IsTrue(allowed.MayProceed, "An unrestricted symbol must still fire in BoundedAuto.");
    }

    [TestMethod]
    public async Task TradingManager_RefusesUnattendedOrder_ForConfiguredManualOnlySymbol()
    {
        using var env = Env.Create(allowed: ["OGDC"], manualOnly: ["OGDC"]);

        // No authorization at all — every unattended worker submits this way in Paper/BoundedAuto.
        var refused = await env.CreateManager().ExecuteGroupsAsync(Groups("OGDC"), "worker:test");
        Assert.IsFalse(refused.Executed);
        StringAssert.Contains(refused.Reason, "manual-only");
        Assert.IsFalse(env.Broker.WasCalled);
    }

    [TestMethod]
    public async Task TradingManager_RefusesPreAuthorizedOrder_AndAllowsAnAttendedOne()
    {
        using var env = Env.Create(allowed: ["OGDC"], manualOnly: ["OGDC"]);
        var manager = env.CreateManager();

        // A pre-authorization carries a real intent and the same method string a human approval does,
        // so attendance is the only thing separating them — the case this flag exists to catch.
        var preAuthorized = await manager.ExecuteGroupsAsync(
            Groups("OGDC"), "strategy:test",
            ExecutionAuthorization.PreAuthorized("approval-auto"));
        Assert.IsFalse(preAuthorized.Executed);
        StringAssert.Contains(preAuthorized.Reason, "manual-only");

        // And the operator is not locked out of their own symbol, which would make the flag useless.
        var byHand = await manager.ExecuteGroupsAsync(
            Groups("OGDC"), "dashboard-order:test",
            ExecutionAuthorization.Attendant("operator"));
        Assert.IsTrue(byHand.Executed, byHand.Reason);
    }

    [TestMethod]
    public async Task TradingManager_RefusesUnattendedOrder_WhenTheDenyComesFromTheWatchlist()
    {
        using var env = Env.Create(allowed: ["OGDC"], manualOnly: []);
        await env.Universe.SeedIfNeededAsync();
        await env.Repository.UpdateWatchlistSymbolAsync(
            "OGDC", alertsEnabled: null, notes: null, pinned: null, autoTradeEnabled: false);
        env.Universe.Invalidate();

        // The runtime half of the deny set, end to end: the boundary reads through to the watchlist
        // rather than trusting a cached snapshot, so a toggle takes effect on the next order.
        var refused = await env.CreateManager().ExecuteGroupsAsync(Groups("OGDC"), "worker:test");
        Assert.IsFalse(refused.Executed);
        StringAssert.Contains(refused.Reason, "manual-only");
    }

    [TestMethod]
    public async Task TradingManager_LeavesUnrestrictedSymbolsAlone()
    {
        using var env = Env.Create(allowed: ["OGDC", "HBL"], manualOnly: ["OGDC"]);

        // The gate is per symbol, not a mode: an unattended order for anything else still executes.
        var executed = await env.CreateManager().ExecuteGroupsAsync(Groups("HBL"), "worker:test");
        Assert.IsTrue(executed.Executed, executed.Reason);
    }

    private static IReadOnlyList<IReadOnlyList<TradingSignal>> Groups(string symbol) =>
    [
        new List<TradingSignal>
        {
            new()
            {
                Action = "BUY", Symbol = symbol, Quantity = 10,
                EntryPrice = 100, OrderType = "LIMIT", Confidence = "HIGH"
            }
        }
    ];

    /// <summary>
    /// A real SQLite ledger and a real universe — the watchlist half of the deny set is a database
    /// column, so a stubbed repository would test the wrong thing. Paper mode keeps the broker out.
    /// </summary>
    private sealed class Env : IDisposable
    {
        public required TradingAgentOptions Configured { get; init; }
        public required IOptions<TradingAgentOptions> Options { get; init; }
        public required SqliteTradingRepository Repository { get; init; }
        public required MonitoredUniverse Universe { get; init; }
        public required TradingPolicyProvider Policy { get; init; }
        public required RecordingBroker Broker { get; init; }
        public required string TempPath { get; init; }

        public static Env Create(string[] allowed, string[] manualOnly, string mode = "Paper")
        {
            var temp = Path.Combine(Path.GetTempPath(), $"agentfox-manual-only-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temp);

            var configured = new TradingAgentOptions
            {
                AutoExecute = true,
                ExecutionMode = mode,
                DatabasePath = Path.Combine(temp, "trading.db"),
                AllowedSymbols = [.. allowed],
                ManualOnlySymbols = [.. manualOnly]
            };
            var options = Microsoft.Extensions.Options.Options.Create(configured);
            var repository = new SqliteTradingRepository(
                options, new ConfigurationBuilder().Build(),
                NullLogger<SqliteTradingRepository>.Instance);

            return new Env
            {
                Configured = configured,
                Options = options,
                Repository = repository,
                Universe = new MonitoredUniverse(
                    options, repository, NullLogger<MonitoredUniverse>.Instance),
                Policy = new TradingPolicyProvider(
                    options,
                    new PluginConfigManager(
                        Path.Combine(temp, "plugin-config"),
                        NullLogger<PluginConfigManager>.Instance)),
                Broker = new RecordingBroker(),
                TempPath = temp
            };
        }

        public TradingManager CreateManager() => new(
            Broker, Repository, new AlwaysOpenCalendar(),
            TradingTestFactory.CalendarOnlyWindow(new AlwaysOpenCalendar()), Policy,
            new TradingRiskEngine(
                Microsoft.Extensions.Options.Options.Create(new AhkConfig()), Options),
            new TradingReconciliationState(), new ApprovalIntentRegistry(), Options,
            NullLogger<TradingManager>.Instance,
            universe: Universe);

        public ApprovalGate CreateApprovalGate() => new(
            new ApprovalIntentRegistry(), Policy, new AlwaysOpenCalendar(),
            TradingTestFactory.CalendarOnlyWindow(new AlwaysOpenCalendar()), Options,
            NullLogger<ApprovalGate>.Instance, Universe);

        public void Dispose()
        {
            try { Directory.Delete(TempPath, recursive: true); } catch { /* temp dir */ }
        }
    }

    private sealed class AlwaysOpenCalendar : IMarketCalendar
    {
        public MarketStatus GetStatus(DateTime? utcNow = null) =>
            new(true, DateTime.UtcNow.AddHours(5), "open");
    }

    private sealed class RecordingBroker : IBrokerAdapter
    {
        public bool WasCalled { get; private set; }

        public Task<IReadOnlyDictionary<string, decimal?>> GetMarketPricesAsync(
            IReadOnlyList<string> symbols) =>
            Task.FromResult<IReadOnlyDictionary<string, decimal?>>(new Dictionary<string, decimal?>());

        public Task<IReadOnlyList<IReadOnlyList<OrderResult>>> PlaceOrderGroupsAsync(
            IReadOnlyList<IReadOnlyList<TradingSignal>> groups)
        {
            WasCalled = true;
            IReadOnlyList<IReadOnlyList<OrderResult>> results = groups
                .Select(group => (IReadOnlyList<OrderResult>)group.Select(signal => new OrderResult
                {
                    Success = true,
                    OrderId = "test-order",
                    Action = signal.Action,
                    Symbol = signal.Symbol,
                    Quantity = signal.Quantity,
                    RequestedPrice = signal.EntryPrice,
                    SubmittedPrice = signal.EntryPrice,
                    Message = "accepted"
                }).ToList())
                .ToList();
            return Task.FromResult(results);
        }
    }
}
