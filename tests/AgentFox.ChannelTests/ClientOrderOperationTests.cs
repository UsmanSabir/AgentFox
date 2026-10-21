using AgentFox.Plugins;
using Microsoft.Data.Sqlite;
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

namespace AgentFox.ChannelTests;

[TestClass]
public sealed class ClientOrderOperationTests
{
    private string _root = "";
    private string DbPath => Path.Combine(_root, "trading.db");
    private const string Hash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static string Id(DateTimeOffset? at = null) => $"op1_{(at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds()}_{Guid.NewGuid():N}";
    private static TradingSignal Order(int quantity = 10, string side = "BUY") => new()
    { Symbol = "TEST", Action = side, Quantity = quantity, EntryPrice = 100, OrderType = "LIMIT" };

    [TestInitialize]
    public void Setup() { _root = Path.Combine(Path.GetTempPath(), "agentfox-client-operation-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Path.GetFullPath(_root).StartsWith(Path.Combine(Path.GetTempPath(), "agentfox-client-operation-"), StringComparison.OrdinalIgnoreCase))
            Directory.Delete(_root, true);
    }
    private SqliteTradingRepository Repository() => new(Options.Create(new TradingAgentOptions { DatabasePath = DbPath }),
        new ConfigurationBuilder().Build(), NullLogger<SqliteTradingRepository>.Instance);
    private async Task Sql(string text, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqliteConnection($"Data Source={DbPath};Pooling=False"); await connection.OpenAsync();
        var command = connection.CreateCommand(); command.CommandText = text;
        foreach (var p in parameters) command.Parameters.AddWithValue(p.Name, p.Value);
        await command.ExecuteNonQueryAsync();
    }

    [TestMethod]
    public async Task ConcurrentClaimsAcrossRepositoryInstancesCreateOneExecutionAndDetectChangedRequests()
    {
        var first = Repository(); var second = Repository(); await first.GetStatusAsync(); await second.GetStatusAsync();
        var id = Id();
        var claims = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() =>
            (i % 2 == 0 ? first : second).TryBeginClientOrderOperationAsync(id, Hash, "[[{}]]", "policy-" + i))));
        Assert.AreEqual(1, claims.Count(c => c.Acquired)); Assert.AreEqual(1, claims.Select(c => c.Operation.ExecutionId).Distinct().Count());
        Assert.AreEqual(1, (await first.GetExecutionsAsync()).Count);
        var replay = await second.TryBeginClientOrderOperationAsync(id, Hash, "different effective sizing", "changed-policy");
        Assert.IsFalse(replay.Acquired); Assert.IsFalse(replay.Conflict);
        var conflict = await first.TryBeginClientOrderOperationAsync(id, new string('B', 64), "different input", "policy");
        Assert.IsTrue(conflict.Conflict); Assert.IsFalse(conflict.Acquired); Assert.AreEqual(replay.Operation.ExecutionId, conflict.Operation.ExecutionId);
    }

    [TestMethod]
    public async Task RestartLookupFindsAnOperationOutsideTheLatestExecutionPageAndNeverCreatesOne()
    {
        var repo = Repository(); var id = Id(); var claim = await repo.TryBeginClientOrderOperationAsync(id, Hash, "[]", "policy");
        await repo.CompleteExecutionAsync(claim.Operation.ExecutionId, "accepted", "{}");
        await Sql("""
            UPDATE trading_executions SET created_utc='2020-01-01T00:00:00.0000000Z' WHERE client_operation_id=$id;
            WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<501)
            INSERT INTO trading_executions (execution_id,idempotency_key,state,request_json,policy_version,created_utc,updated_utc)
            SELECT 'other-'||x,'key-'||x,'accepted','[]','p','2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z' FROM n;
            """, ("$id", id));
        Assert.IsFalse((await repo.GetExecutionsAsync(500)).Any(e => e.ExecutionId == claim.Operation.ExecutionId));
        var restarted = Repository(); var found = await restarted.FindClientOrderOperationAsync(id);
        Assert.IsNotNull(found); Assert.AreEqual("accepted", found.State); Assert.AreEqual(claim.Operation.ExecutionId, found.ExecutionId);
        Assert.IsNull(await restarted.FindClientOrderOperationAsync(Id()));
        Assert.AreEqual(502, (await restarted.GetStatusAsync()).AcceptedExecutions);
    }

    [TestMethod]
    public async Task RetentionPreservesLiveIdAndUnknownThenExpiredPrunedIdCannotSubmitAgain()
    {
        var repo = Repository(); var live = await repo.TryBeginClientOrderOperationAsync(Id(), Hash, "[]", "p");
        var expiredId = Id(DateTimeOffset.UtcNow.AddDays(-2));
        var old = await repo.TryBeginExecutionAsync("old", "[]", "p"); await repo.CompleteExecutionAsync(old.ExecutionId, "accepted", "{}");
        var unknown = await repo.TryBeginExecutionAsync("uncertain", "[]", "p"); await repo.CompleteExecutionAsync(unknown.ExecutionId, "unknown", "{}");
        ClientOrderRequestId.TryGetExpiry(expiredId, out var expiry);
        await Sql("UPDATE trading_executions SET client_operation_id=$id,client_request_hash=$hash,client_operation_expires_utc=$expiry WHERE execution_id=$execution",
            ("$id", expiredId), ("$hash", Hash), ("$expiry", expiry.ToString("O")), ("$execution", old.ExecutionId));
        await repo.CompleteExecutionAsync(live.Operation.ExecutionId, "accepted", "{}");
        await Sql("UPDATE trading_executions SET created_utc=$old,updated_utc=$old", ("$old", DateTime.UtcNow.AddDays(-30).ToString("O")));
        Assert.AreEqual(1, (await repo.PruneExecutionsAsync(DateTime.UtcNow.AddDays(-14))).Removed);
        Assert.IsNotNull(await repo.FindClientOrderOperationAsync(live.Operation.RequestId));
        Assert.IsNull(await repo.FindClientOrderOperationAsync(expiredId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.TryBeginClientOrderOperationAsync(expiredId, Hash, "[]", "p"));
        Assert.AreEqual(1, (await repo.GetExecutionsAsync()).Count(e => e.State == "unknown"));
    }

    [TestMethod]
    public async Task ExistingDatabaseMigratesNullableOperationColumnsWithoutAssociatingLegacyExecutions()
    {
        await Sql("""
            CREATE TABLE trading_executions(execution_id TEXT PRIMARY KEY,idempotency_key TEXT NOT NULL UNIQUE,
                state TEXT NOT NULL,request_json TEXT NOT NULL,result_json TEXT NULL,policy_version TEXT NOT NULL,created_utc TEXT NOT NULL,updated_utc TEXT NOT NULL);
            INSERT INTO trading_executions VALUES('legacy','key','accepted','[]',NULL,'old','2026-01-01T00:00:00Z','2026-01-01T00:00:00Z');
            """);
        var repo = Repository(); var id = Id();
        Assert.IsNull(await repo.FindClientOrderOperationAsync(id));
        Assert.IsTrue((await repo.TryBeginClientOrderOperationAsync(id, Hash, "[]", "p")).Acquired);
        Assert.AreEqual(2, (await repo.GetExecutionsAsync()).Count);
    }

    [TestMethod]
    public async Task InvalidFutureAndExpiredIdsCannotCreateClaims()
    {
        var repo = Repository();
        foreach (var id in new[] { "", "op1_foo", Id().ToUpperInvariant(), "' OR 1=1--" })
            await Assert.ThrowsAsync<ArgumentException>(() => repo.TryBeginClientOrderOperationAsync(id, Hash, "[]", "p"));
        foreach (var id in new[] { Id(DateTimeOffset.UtcNow.AddDays(-2)), Id(DateTimeOffset.UtcNow.AddMinutes(1)) })
            await Assert.ThrowsAsync<InvalidOperationException>(() => repo.TryBeginClientOrderOperationAsync(id, Hash, "[]", "p"));
        Assert.AreEqual(0, (await repo.GetExecutionsAsync()).Count);
    }

    private (TradingManager Manager, SqliteTradingRepository Repository, FakeBroker Broker, TradingAgentOptions Config,
        TradingReconciliationState State, TradingPolicyProvider Policy, ApprovalIntentRegistry Intents) Manager(string mode = "Paper")
    {
        var config = new TradingAgentOptions { AutoExecute = true, ExecutionMode = mode, DatabasePath = DbPath, AllowedSymbols = ["TEST"], RequireReconciliationHealthy = true };
        var options = Options.Create(config); var policy = new TradingPolicyProvider(options,
            new PluginConfigManager(Path.Combine(_root, "config"), NullLogger<PluginConfigManager>.Instance));
        var repo = Repository(); var broker = new FakeBroker(); var calendar = new OpenCalendar(); var state = new TradingReconciliationState();
        state.Update(new(true, true, "synthetic", DateTime.UtcNow) { Positions = [new("TEST", 10)] });
        var intents = new ApprovalIntentRegistry();
        var manager = new TradingManager(broker, repo, calendar, TradingTestFactory.CalendarOnlyWindow(calendar), policy,
            new TradingRiskEngine(Options.Create(new AhkConfig()), options), state, intents, options, NullLogger<TradingManager>.Instance);
        return (manager, repo, broker, config, state, policy, intents);
    }

    [TestMethod]
    public async Task LostReplyReplaysBeforeChangedPolicyAndChangedOrderConflicts()
    {
        var f = Manager(); var id = Id(); var first = await f.Manager.ExecuteClientOrderAsync(Order(), id);
        Assert.IsTrue(first.Executed, first.Reason); f.Config.AutoExecute = false; f.Config.KillSwitch = true;
        var replay = await f.Manager.ExecuteClientOrderAsync(Order(), id);
        Assert.IsTrue(replay.IsReplay); Assert.AreEqual(first.ExecutionId, replay.ExecutionId); Assert.AreEqual(0, f.Broker.Calls);
        await Assert.ThrowsAsync<ClientOrderConflictException>(() => f.Manager.ExecuteClientOrderAsync(Order(11), id));
        Assert.AreEqual(1, (await f.Repository.GetExecutionsAsync()).Count);
    }

    [TestMethod]
    public async Task LiveFakeBrokerIsCalledOnceAndSellReplayUsesOriginalRequestedMaximum()
    {
        var f = Manager("BoundedAuto"); var id = Id();
        var first = await f.Manager.ExecuteClientOrderAsync(Order(20, "SELL"), id);
        Assert.IsTrue(first.Executed, first.Reason); Assert.AreEqual(10, f.Broker.LastQuantity);
        f.State.Update(new(true, true, "changed custody", DateTime.UtcNow) { Positions = [new("TEST", 3)] });
        var replay = await f.Manager.ExecuteClientOrderAsync(Order(20, "SELL"), id);
        Assert.IsTrue(replay.IsReplay); Assert.AreEqual(first.ExecutionId, replay.ExecutionId); Assert.AreEqual(1, f.Broker.Calls);
    }

    [TestMethod]
    public async Task BrokerExceptionRemainsUnknownAndOperatorResolutionNeverResubmits()
    {
        var f = Manager("BoundedAuto"); f.Broker.Fail = true; var id = Id();
        var first = await f.Manager.ExecuteClientOrderAsync(Order(), id);
        Assert.AreEqual("unknown", (await f.Repository.FindClientOrderOperationAsync(id))!.State);
        Assert.IsTrue((await f.Manager.ExecuteClientOrderAsync(Order(), id)).IsReplay); Assert.AreEqual(1, f.Broker.Calls);
        Assert.IsTrue(await f.Repository.ResolveUnknownExecutionAsync(first.ExecutionId, "not_placed", "{\"note\":\"synthetic broker check\"}"));
        var resolved = await f.Manager.ExecuteClientOrderAsync(Order(), id);
        Assert.IsTrue(resolved.IsReplay); Assert.IsFalse(resolved.Executed); StringAssert.Contains(resolved.Reason, "resolved_not_placed");
        Assert.AreEqual(1, f.Broker.Calls);
    }

    [TestMethod]
    public async Task NewOperationsStillRequireRiskPolicyAndAtomicLedgerWriteBeforeBrokerAccess()
    {
        var f = Manager("BoundedAuto"); f.Config.KillSwitch = true;
        Assert.IsFalse((await f.Manager.ExecuteClientOrderAsync(Order(), Id())).Executed); Assert.AreEqual(0, f.Broker.Calls);
        f.Config.KillSwitch = false; await f.Repository.GetStatusAsync();
        await Sql("CREATE TRIGGER reject_operation BEFORE INSERT ON trading_executions WHEN NEW.client_operation_id IS NOT NULL BEGIN SELECT RAISE(ABORT,'synthetic write failure'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => f.Manager.ExecuteClientOrderAsync(Order(), Id()));
        Assert.AreEqual(0, f.Broker.Calls); Assert.AreEqual(0, (await f.Repository.GetExecutionsAsync()).Count);
    }

    [TestMethod]
    public async Task AConcurrentReplayReportsSubmittingWithoutStartingASecondBrokerCall()
    {
        var f = Manager("BoundedAuto"); var id = Id();
        f.Broker.Pause = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = f.Manager.ExecuteClientOrderAsync(Order(), id);
        try
        {
            await f.Broker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var replay = await f.Manager.ExecuteClientOrderAsync(Order(), id);
            Assert.IsTrue(replay.IsReplay); Assert.IsFalse(replay.Executed); StringAssert.Contains(replay.Reason, "submitting");
            Assert.AreEqual(1, f.Broker.Calls); Assert.AreEqual("submitting", (await f.Repository.FindClientOrderOperationAsync(id))!.State);
        }
        finally { f.Broker.Pause.TrySetResult(); await first; }
        Assert.IsTrue((await first).Executed); Assert.AreEqual(1, f.Broker.Calls);
    }

    [TestMethod]
    public async Task ValidApprovalRemainsBoundToTheExactOrderWhileReplayConsumesNoSecondApproval()
    {
        var f = Manager("ApprovalRequired"); var id = Id(); var order = Order();
        var intent = ApprovalIntent.Create([[order]], ClientOrderRequestId.SourceMessage(id), f.Policy.Current().Version, TimeSpan.FromMinutes(2));
        f.Intents.Register(intent);
        var first = await f.Manager.ExecuteClientOrderAsync(order, id, ExecutionAuthorization.HostToolGate("synthetic", intent));
        Assert.IsTrue(first.Executed, first.Reason); Assert.AreEqual(1, f.Broker.Calls);
        Assert.IsTrue((await f.Manager.ExecuteClientOrderAsync(Order(), id)).IsReplay); Assert.AreEqual(1, f.Broker.Calls);
    }

    [TestMethod]
    [DataRow("disabled")]
    [DataRow("approval")]
    [DataRow("reconciliation")]
    public async Task NewClientPathPreservesEachExecutionGate(string gate)
    {
        var f = Manager("BoundedAuto");
        if (gate == "disabled") f.Config.AutoExecute = false;
        if (gate == "approval") f.Config.ExecutionMode = "ApprovalRequired";
        if (gate == "reconciliation") f.State.Update(new(true, false, "synthetic unavailable", DateTime.UtcNow));
        var id = Id(); var result = await f.Manager.ExecuteClientOrderAsync(Order(), id);
        Assert.IsFalse(result.Executed); Assert.AreEqual(0, f.Broker.Calls);
        Assert.IsNull(await f.Repository.FindClientOrderOperationAsync(id));
    }

    private sealed class OpenCalendar : IMarketCalendar
    { public MarketStatus GetStatus(DateTime? utcNow = null) => new(true, DateTime.UtcNow.AddHours(5), "synthetic open"); }
    private sealed class FakeBroker : IBrokerAdapter
    {
        public int Calls, LastQuantity; public bool Fail;
        public TaskCompletionSource? Pause;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyDictionary<string, decimal?>> GetMarketPricesAsync(IReadOnlyList<string> symbols) => Task.FromResult<IReadOnlyDictionary<string, decimal?>>(new Dictionary<string, decimal?>());
        public async Task<IReadOnlyList<IReadOnlyList<OrderResult>>> PlaceOrderGroupsAsync(IReadOnlyList<IReadOnlyList<TradingSignal>> groups)
        {
            Interlocked.Increment(ref Calls); LastQuantity = groups[0][0].Quantity ?? 0;
            Started.TrySetResult(); if (Pause is not null) await Pause.Task;
            if (Fail) throw new InvalidOperationException("synthetic dropped reply");
            return [[new() { Success = true, Symbol = "TEST", Action = groups[0][0].Action, Quantity = LastQuantity, OrderId = "synthetic-order" }]];
        }
    }
}
