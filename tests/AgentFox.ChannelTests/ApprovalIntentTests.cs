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
using TradingAgent.Risk;
using TradingAgent.Reconciliation;

namespace AgentFox.ChannelTests;

/// <summary>
/// Phase 3 hardening: an ApprovalRequired execution must carry an immutable, one-time,
/// expiring approval intent whose integrity hash TradingManager revalidates immediately
/// before broker submission. A changed order, changed policy, expired intent, or replayed
/// intent is rejected before the broker is touched.
/// </summary>
[TestClass]
public sealed class ApprovalIntentTests
{
    private static IReadOnlyList<IReadOnlyList<TradingSignal>> Groups(int quantity = 10, decimal price = 100) =>
    [
        new List<TradingSignal>
        {
            new() { Action = "BUY", Symbol = "OGDC", Quantity = quantity, EntryPrice = price, OrderType = "LIMIT" }
        }
    ];

    [TestMethod]
    public void ComputeHash_IsStableForEqualOrdersAndDetectsTampering()
    {
        var baseline = ApprovalIntent.ComputeHash(Groups(), "msg", "v1");

        Assert.AreEqual(baseline, ApprovalIntent.ComputeHash(Groups(), "msg", "v1"));
        Assert.AreNotEqual(baseline, ApprovalIntent.ComputeHash(Groups(quantity: 11), "msg", "v1"));
        Assert.AreNotEqual(baseline, ApprovalIntent.ComputeHash(Groups(price: 101), "msg", "v1"));
        Assert.AreNotEqual(baseline, ApprovalIntent.ComputeHash(Groups(), "msg", "v2"));
        Assert.AreNotEqual(baseline, ApprovalIntent.ComputeHash(Groups(), "other-msg", "v1"));
    }

    [TestMethod]
    public void Registry_ConsumesAnIntentExactlyOnce()
    {
        var registry = new ApprovalIntentRegistry();
        var intent = ApprovalIntent.Create(Groups(), "msg", "v1", TimeSpan.FromMinutes(2));
        registry.Register(intent);

        Assert.IsTrue(registry.TryConsume(intent.IntentId, out var consumed));
        Assert.AreEqual(intent.IntegrityHash, consumed!.IntegrityHash);
        Assert.IsFalse(registry.TryConsume(intent.IntentId, out _));
    }

    [TestMethod]
    public async Task ApprovalRequired_RejectsExecutionWithoutIntent()
    {
        var env = TestEnvironment.Create();
        try
        {
            var result = await env.Manager.ExecuteGroupsAsync(
                Groups(), "no-intent", ExecutionAuthorization.HostToolGate());

            Assert.IsFalse(result.Executed);
            Assert.IsFalse(env.Broker.WasCalled);
            StringAssert.Contains(result.Reason, "approval intent");
        }
        finally { env.Dispose(); }
    }

    [TestMethod]
    public async Task ApprovalRequired_RejectsOrdersModifiedAfterApproval()
    {
        var env = TestEnvironment.Create();
        try
        {
            var intent = ApprovalIntent.Create(
                Groups(quantity: 10), "tamper-source", env.PolicyVersion, TimeSpan.FromMinutes(2));
            env.Registry.Register(intent);

            var result = await env.Manager.ExecuteGroupsAsync(
                Groups(quantity: 100), "tamper-source",
                ExecutionAuthorization.HostToolGate(intent: intent));

            Assert.IsFalse(result.Executed);
            Assert.IsFalse(env.Broker.WasCalled);
            StringAssert.Contains(result.Reason, "integrity hash");
        }
        finally { env.Dispose(); }
    }

    [TestMethod]
    public async Task ApprovalRequired_RejectsExpiredIntent()
    {
        var env = TestEnvironment.Create();
        try
        {
            var intent = ApprovalIntent.Create(
                Groups(), "expired-source", env.PolicyVersion, TimeSpan.FromSeconds(-1));
            env.Registry.Register(intent);

            var result = await env.Manager.ExecuteGroupsAsync(
                Groups(), "expired-source", ExecutionAuthorization.HostToolGate(intent: intent));

            Assert.IsFalse(result.Executed);
            Assert.IsFalse(env.Broker.WasCalled);
            StringAssert.Contains(result.Reason, "expired");
        }
        finally { env.Dispose(); }
    }

    [TestMethod]
    public async Task ApprovalRequired_ExecutesValidIntentOnceAndRejectsReplay()
    {
        var env = TestEnvironment.Create();
        try
        {
            var intent = ApprovalIntent.Create(
                Groups(), "replay-source", env.PolicyVersion, TimeSpan.FromMinutes(2));
            env.Registry.Register(intent);

            var first = await env.Manager.ExecuteGroupsAsync(
                Groups(), "replay-source", ExecutionAuthorization.HostToolGate(intent: intent));
            Assert.IsTrue(first.Executed, first.Reason);
            Assert.IsTrue(env.Broker.WasCalled);

            var replay = await env.Manager.ExecuteGroupsAsync(
                Groups(), "replay-source", ExecutionAuthorization.HostToolGate(intent: intent));
            Assert.IsFalse(replay.Executed);
            StringAssert.Contains(replay.Reason, "already consumed");
        }
        finally { env.Dispose(); }
    }

    // ── Shared fixture ────────────────────────────────────────────────────────

    private sealed class TestEnvironment : IDisposable
    {
        public required TradingAgent.Manager.TradingManager Manager { get; init; }
        public required ApprovalIntentRegistry Registry { get; init; }
        public required RecordingBroker Broker { get; init; }
        public required string PolicyVersion { get; init; }
        public required string TempPath { get; init; }

        public static TestEnvironment Create()
        {
            var temp = Path.Combine(Path.GetTempPath(), $"agentfox-intent-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temp);

            var options = Options.Create(new TradingAgentOptions
            {
                AutoExecute = true,
                ExecutionMode = "ApprovalRequired",
                DatabasePath = Path.Combine(temp, "trading.db"),
                AllowedSymbols = ["OGDC"],
                RequireReconciliationHealthy = false
            });
            var pluginConfig = new PluginConfigManager(
                Path.Combine(temp, "plugin-config"), NullLogger<PluginConfigManager>.Instance);
            var policyProvider = new TradingPolicyProvider(options, pluginConfig);
            var repository = new SqliteTradingRepository(
                options, new ConfigurationBuilder().Build(), NullLogger<SqliteTradingRepository>.Instance);
            var broker = new RecordingBroker();
            var registry = new ApprovalIntentRegistry();
            var manager = new TradingAgent.Manager.TradingManager(
                broker, repository, new AlwaysOpenCalendar(), policyProvider,
                new TradingRiskEngine(Options.Create(new AhkConfig()), options),
                new TradingReconciliationState(), registry, options,
                NullLogger<TradingAgent.Manager.TradingManager>.Instance);

            return new TestEnvironment
            {
                Manager = manager,
                Registry = registry,
                Broker = broker,
                PolicyVersion = policyProvider.Current().Version,
                TempPath = temp
            };
        }

        public void Dispose()
        {
            try { Directory.Delete(TempPath, recursive: true); } catch { /* best effort */ }
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

        public Task<IReadOnlyDictionary<string, decimal?>> GetMarketPricesAsync(IReadOnlyList<string> symbols) =>
            Task.FromResult<IReadOnlyDictionary<string, decimal?>>(new Dictionary<string, decimal?>());

        public Task<IReadOnlyList<IReadOnlyList<OrderResult>>> PlaceOrderGroupsAsync(
            IReadOnlyList<IReadOnlyList<TradingSignal>> groups)
        {
            WasCalled = true;
            return Task.FromResult<IReadOnlyList<IReadOnlyList<OrderResult>>>([]);
        }
    }
}
