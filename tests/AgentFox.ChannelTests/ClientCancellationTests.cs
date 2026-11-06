using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Manager;
using TradingAgent.Persistence;
using TradingAgent.Reconciliation;
using TradingAgent.Trading;
using TradingAgent.Broker;

namespace AgentFox.ChannelTests;

[TestClass]
public sealed class ClientCancellationTests
{
    private string root = "";
    private static string Id() => $"op1_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_{Guid.NewGuid():N}";
    private SqliteTradingRepository Repository() => new(Options.Create(new TradingAgentOptions { DatabasePath = Path.Combine(root, "test.db") }),
        new ConfigurationBuilder().Build(), NullLogger<SqliteTradingRepository>.Instance);
    [TestInitialize] public void Setup() { root = Path.Combine(Path.GetTempPath(), "agentfox-client-cancel-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); }
    [TestCleanup] public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        var full = Path.GetFullPath(root);
        if (full.StartsWith(Path.Combine(Path.GetTempPath(), "agentfox-client-cancel-"), StringComparison.OrdinalIgnoreCase)) Directory.Delete(full, true);
    }
    [TestMethod]
    public async Task AtomicKindsShareOneClaimNamespaceEvenWithIdenticalHashesAndRestart()
    {
        var first = Repository(); var second = Repository(); await first.GetStatusAsync(); await second.GetStatusAsync();
        var id = Id(); var hash = new string('A', 64);
        var claims = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() => i % 2 == 0
            ? first.TryBeginClientOrderOperationAsync(id, hash, "[]", "p")
            : second.TryBeginClientActionOperationAsync("cancel", id, hash, "{}", "p"))));
        Assert.AreEqual(1, claims.Count(c => c.Acquired)); Assert.AreEqual(6, claims.Count(c => c.Conflict));
        var kind = claims.Single(c => c.Acquired).Operation.Kind;
        Assert.AreEqual(kind, (await Repository().FindClientOrderOperationAsync(id))!.Kind);
        Assert.AreEqual(1, (await first.GetExecutionsAsync()).Count);
    }
    [TestMethod]
    public async Task ConcurrentCancellationReplaysAndRestartNeverInvokeTheBrokerTwice()
    {
        var repo = Repository(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        var service = new ClientCancellationService(repo, async (_, _, _, _) => { Interlocked.Increment(ref calls); entered.SetResult(); await release.Task; return new("cancelled", "private venue text"); });
        var id = Id(); var request = new WorkingOrderCancelRequest("order", "TEST");
        var pending = service.ExecuteAsync(request, id, "verified-account-scope", "operator");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var replay = await service.ExecuteAsync(request, id, "verified-account-scope", "operator");
        Assert.IsTrue(replay.IsReplay); Assert.AreEqual("submitting", replay.State); Assert.AreEqual("unconfirmed", replay.Outcome);
        release.SetResult(); var result = await pending; Assert.AreEqual("cancelled", result.Outcome); Assert.AreEqual("accepted", result.State);
        var restart = new ClientCancellationService(Repository(), (_, _, _, _) => throw new AssertFailedException("Replay reached broker"));
        Assert.AreEqual("cancelled", (await restart.ExecuteAsync(request, id, "verified-account-scope", "operator")).Outcome); Assert.AreEqual(1, calls);
        Assert.IsFalse((await repo.FindClientOrderOperationAsync(id))!.ResultJson!.Contains("private"));
        await Assert.ThrowsAsync<ClientOrderConflictException>(() => restart.ExecuteAsync(request with { Symbol = "OTHER" }, id, "verified-account-scope", "operator"));
        await Assert.ThrowsAsync<ClientOrderConflictException>(() => restart.ExecuteAsync(request, id, "other-account", "operator"));
        await Assert.ThrowsAsync<ClientOrderConflictException>(() => restart.ExecuteAsync(request, id, "verified-account-scope", "other-actor"));
    }
    [TestMethod]
    public async Task UnknownCancelNeedsKindSpecificAuditedResolutionAndRetainsOriginalResult()
    {
        var repo = Repository(); var calls = 0;
        var service = new ClientCancellationService(repo, (_, _, _, _) => { calls++; throw new IOException("lost private reply"); });
        var id = Id(); var request = new WorkingOrderCancelRequest("order", "TEST");
        var result = await service.ExecuteAsync(request, id, "scope", "operator"); Assert.AreEqual("unknown", result.State);
        var before = (await repo.FindClientOrderOperationAsync(id))!.ResultJson;
        Assert.IsFalse(await repo.ResolveUnknownExecutionAsync(result.ExecutionId, "placed", "{}"));
        Assert.IsTrue(await repo.ResolveUnknownExecutionAsync(result.ExecutionId, "filled", "{\"note\":\"synthetic broker verification\"}"));
        var replay = await service.ExecuteAsync(request, id, "scope", "operator");
        Assert.AreEqual("resolved_filled", replay.Outcome); Assert.AreEqual(1, calls);
        Assert.AreEqual(before, (await repo.FindClientOrderOperationAsync(id))!.ResultJson);
        Assert.IsTrue((await repo.GetEventsAsync()).Any(e => e.ExecutionId == result.ExecutionId && e.EventType == "unknown_resolved"));
        Assert.IsFalse(await repo.ResolveUnknownExecutionAsync(result.ExecutionId, "cancelled", "{}"));
    }
    [TestMethod]
    public async Task FailedDurableClaimAndExpiredIdCannotReachCancellation()
    {
        var repo = Repository(); await repo.GetStatusAsync(); var calls = 0;
        var service = new ClientCancellationService(repo, (_, _, _, _) => { calls++; return Task.FromResult(new WorkingOrderCancelResult("cancelled", "synthetic")); });
        await using var db = new SqliteConnection($"Data Source={Path.Combine(root, "test.db")};Pooling=False"); await db.OpenAsync();
        var command = db.CreateCommand(); command.CommandText = "CREATE TRIGGER reject_cancel BEFORE INSERT ON trading_executions WHEN NEW.client_operation_kind='cancel' BEGIN SELECT RAISE(ABORT,'synthetic failure'); END;"; await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<SqliteException>(() => service.ExecuteAsync(new("order", "TEST"), Id(), "scope", "operator")); Assert.AreEqual(0, calls);
        var expired = $"op1_{DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeSeconds()}_{Guid.NewGuid():N}";
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync(new("order", "TEST"), expired, "scope", "operator")); Assert.AreEqual(0, calls);
    }
    [TestMethod]
    public async Task FailedResultSaveLeavesSubmittingAndCannotRepeatAKnownBrokerCancellation()
    {
        var repo = Repository(); await repo.GetStatusAsync(); var calls = 0;
        await using var db = new SqliteConnection($"Data Source={Path.Combine(root, "test.db")};Pooling=False"); await db.OpenAsync();
        var command = db.CreateCommand(); command.CommandText = "CREATE TRIGGER reject_cancel_complete BEFORE UPDATE OF state ON trading_executions WHEN OLD.client_operation_kind='cancel' BEGIN SELECT RAISE(ABORT,'synthetic failure'); END;"; await command.ExecuteNonQueryAsync();
        var service = new ClientCancellationService(repo, (_, _, _, _) => { calls++; return Task.FromResult(new WorkingOrderCancelResult("cancelled", "synthetic")); });
        var id = Id(); var request = new WorkingOrderCancelRequest("order", "TEST");
        Assert.AreEqual("unknown", (await service.ExecuteAsync(request, id, "scope", "operator")).State);
        Assert.AreEqual("submitting", (await Repository().FindClientOrderOperationAsync(id))!.State);
        Assert.AreEqual("submitting", (await service.ExecuteAsync(request, id, "scope", "operator")).State); Assert.AreEqual(1, calls);
    }
    [TestMethod]
    public async Task RealCancelServiceChecksFreshTargetAndOwnersBeforeDispatchAndAcceptsRemainingQuantityReduction()
    {
        var row = new BrokerWorkingOrder("order", "TEST", "BUY", 10, 12, "LIMIT");
        var reader = new Reader { Rows = [row with { RemainingQuantity = 3 }] }; var canceller = new Canceller();
        var service = new WorkingOrderCancellationService(reader, new TradingReconciliationState(), Repository(), canceller, null!, NullLogger<WorkingOrderCancellationService>.Instance);
        var request = new WorkingOrderCancelRequest("order", "TEST", true, row, []);
        var owner = new WorkingOrderOwner(WorkingOrderOwnerKind.Edition, "new-campaign", "Automation may replace an order");
        Assert.AreEqual("needs_acknowledgement", (await service.CancelAsync(request, "synthetic", (_, _) => Task.FromResult<IReadOnlyList<WorkingOrderOwner>>([owner]))).Outcome);
        reader.Rows = [row with { Price = 13 }]; Assert.AreEqual("needs_acknowledgement", (await service.CancelAsync(request, "synthetic")).Outcome);
        reader.Rows = [row, row]; Assert.AreEqual("ambiguous", (await service.CancelAsync(request, "synthetic")).Outcome); Assert.AreEqual(0, canceller.Calls);
        reader.Rows = [row with { RemainingQuantity = 3 }]; Assert.AreEqual("cancelled", (await service.CancelAsync(request, "synthetic")).Outcome); Assert.AreEqual(1, canceller.Calls);
    }
    private sealed class Reader : IBrokerStateReader
    {
        public IReadOnlyList<BrokerWorkingOrder> Rows = [];
        public Task<BrokerReconciliationSnapshot> ReadSnapshotAsync(CancellationToken ct = default) =>
            Task.FromResult(new BrokerReconciliationSnapshot(true, true, "synthetic", DateTime.UtcNow) { OpenOrders = Rows });
    }
    private sealed class Canceller : IBrokerOrderCanceller
    {
        public int Calls;
        public Task<BrokerCancellationResult> CancelOrderAsync(string orderNo, CancellationToken ct = default)
        { Calls++; return Task.FromResult(new BrokerCancellationResult(true, true, true, "synthetic")); }
    }
    [TestMethod]
    public void ReviewedTargetAllowsPartialFillsAndProvenAliasesButRejectsChangedSidePriceTypeAndOwners()
    {
        var expected = new BrokerWorkingOrder("short", "TEST", "SEL", 10, 12, "LIMIT");
        var renumbered = new BrokerWorkingOrder("long-exchange", "TEST", "SELL", 3, 12, "LIMIT", "short");
        Assert.IsTrue(WorkingOrderCancellationService.SameReviewedTarget(expected, renumbered));
        foreach (var changed in new[] { renumbered with { Price = 13 }, renumbered with { Side = "BUY" }, renumbered with { Symbol = "OTHER" },
            renumbered with { OrderType = "STOPLOSS" }, renumbered with { AlternateOrderNo = null } })
            Assert.IsFalse(WorkingOrderCancellationService.SameReviewedTarget(expected, changed));
        var owners = new[] { new WorkingOrderOwner(WorkingOrderOwnerKind.ProtectiveStop, "stop", "Disarms protection") };
        Assert.IsTrue(WorkingOrderCancellationService.SameReviewedOwners(owners, owners));
        Assert.IsFalse(WorkingOrderCancellationService.SameReviewedOwners(owners, [owners[0] with { Id = "other-stop" }]));
        Assert.IsFalse(WorkingOrderCancellationService.SameReviewedOwners([], owners));
    }
    [TestMethod]
    [DataRow("gone_possibly_filled", "failed")]
    [DataRow("not_confirmed", "unknown")]
    [DataRow("attention", "unknown")]
    [DataRow("needs_acknowledgement", "failed")]
    [DataRow("not_resting", "failed")]
    public async Task CancellationStatesNeverUpgradeGoneOrPendingIntoConfirmedCancellation(string outcome, string state)
    {
        var service = new ClientCancellationService(Repository(), (_, _, _, _) => Task.FromResult(new WorkingOrderCancelResult(outcome, "synthetic")));
        var result = await service.ExecuteAsync(new("order", "TEST"), Id(), "scope", "operator");
        Assert.AreEqual(state, result.State); Assert.AreEqual(outcome, result.Outcome);
    }
}
