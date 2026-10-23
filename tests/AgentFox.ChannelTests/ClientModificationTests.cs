using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Manager;
using TradingAgent.Persistence;
using TradingAgent.Trading;

namespace AgentFox.ChannelTests;

[TestClass]
public sealed class ClientModificationTests
{
    private string root = "";
    private static string Id() => $"op1_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_{Guid.NewGuid():N}";
    private SqliteTradingRepository Repository() => new(Options.Create(new TradingAgentOptions { DatabasePath = Path.Combine(root, "test.db") }),
        new ConfigurationBuilder().Build(), NullLogger<SqliteTradingRepository>.Instance);
    [TestInitialize] public void Setup() { root = Path.Combine(Path.GetTempPath(), "agentfox-client-modify-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); }
    [TestCleanup] public void Cleanup() {
        SqliteConnection.ClearAllPools();
        if (Path.GetFullPath(root).StartsWith(Path.Combine(Path.GetTempPath(), "agentfox-client-modify-"), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
    }
    private static Task<ClientModificationResult> Run(ClientModificationService service, string id,
        Func<ClientModificationIds, CancellationToken, Task<string>> callback, string terms = "{\"quantity\":10}", string scope = "verified-scope", string actor = "operator") =>
        service.CancelOriginalAsync(id, scope, actor, terms, callback);

    [TestMethod]
    public void ChildIdsAreDeterministicDistinctAndKeepTheOriginalNewClaimExpiry()
    {
        var id = Id(); var children = ClientModificationIds.From(id);
        Assert.AreEqual(children, ClientModificationIds.From(id)); Assert.AreNotEqual(children.Cancellation, children.Replacement);
        Assert.AreNotEqual(id, children.Cancellation); Assert.AreNotEqual(id, children.Replacement);
        foreach (var child in new[] { children.Cancellation, children.Replacement }) {
            Assert.IsTrue(ClientOrderRequestId.TryGetExpiry(child, out var expiry)); ClientOrderRequestId.TryGetExpiry(id, out var original); Assert.AreEqual(original, expiry);
        }
        Assert.Throws<ArgumentException>(() => ClientModificationIds.From(id + "\n"));
    }
    [TestMethod]
    public async Task ParentClaimsOnceBeforeCancellationAndReplayOrRestartNeverDispatchAgain()
    {
        var repo = Repository(); var service = new ClientModificationService(repo); var calls = 0;
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var id = Id();
        var pending = Run(service, id, async (ids, _) => {
            calls++; Assert.AreEqual("modify", (await repo.FindClientOrderOperationAsync(id))!.Kind);
            Assert.IsNull(await repo.FindClientOrderOperationAsync(ids.Replacement)); arrived.SetResult(); await release.Task; return "cancelled";
        });
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var replay = await Run(service, id, (_, _) => throw new AssertFailedException("Replay reached cancellation"));
        Assert.AreEqual("submitting", replay.State); Assert.AreEqual("unconfirmed", replay.CancellationOutcome);
        release.SetResult(); var completed = await pending; Assert.AreEqual("accepted", completed.State); Assert.AreEqual("cancelled", completed.CancellationOutcome);
        var restarted = await Run(new(Repository()), id, (_, _) => throw new AssertFailedException("Restart reached cancellation"));
        Assert.IsTrue(restarted.IsReplay); Assert.AreEqual(completed.Operations, restarted.Operations); Assert.AreEqual(1, calls);
        var recorded = (await repo.FindClientOrderOperationAsync(id))!;
        StringAssert.Contains(recorded.RequestJson!, "\"quantity\":10"); StringAssert.Contains(recorded.ResultJson!, "cancelled-awaiting-review");
        Assert.IsNull(await repo.FindClientOrderOperationAsync(completed.Operations.Replacement));
    }
    [TestMethod]
    public async Task DifferentKindsScopeActorOrReviewedTermsCannotReuseTheParent()
    {
        var repo = Repository(); var id = Id(); var service = new ClientModificationService(repo);
        await Run(service, id, (_, _) => Task.FromResult("cancelled"));
        foreach (var args in new[] { ("{\"quantity\":11}", "verified-scope", "operator"), ("{\"quantity\":10}", "other-scope", "operator"), ("{\"quantity\":10}", "verified-scope", "other") })
            await Assert.ThrowsAsync<ClientOrderConflictException>(() => Run(service, id, (_, _) => throw new AssertFailedException(), args.Item1, args.Item2, args.Item3));
        var original = (await repo.FindClientOrderOperationAsync(id))!;
        Assert.IsTrue((await repo.TryBeginClientOrderOperationAsync(id, original.RequestHash, "[]", "p")).Conflict);
        Assert.IsTrue((await repo.TryBeginClientActionOperationAsync("cancel", id, original.RequestHash, "{}", "p")).Conflict);
        await Assert.ThrowsAsync<ArgumentException>(() => repo.TryBeginClientActionOperationAsync("arbitrary", Id(), original.RequestHash, "{}", "p"));
    }
    [TestMethod]
    public async Task FailedSaveAfterCancellationRetainsAnUncertainParentAndBlocksAnotherDispatch()
    {
        var repo = Repository(); await repo.GetStatusAsync();
        await using var db = new SqliteConnection($"Data Source={Path.Combine(root, "test.db")};Pooling=False"); await db.OpenAsync();
        var sql = db.CreateCommand(); sql.CommandText = "CREATE TRIGGER reject_modify_complete BEFORE UPDATE OF state ON trading_executions WHEN OLD.client_operation_kind='modify' BEGIN SELECT RAISE(ABORT,'synthetic'); END;"; await sql.ExecuteNonQueryAsync();
        var id = Id(); var calls = 0; var service = new ClientModificationService(repo);
        var result = await Run(service, id, (_, _) => { calls++; return Task.FromResult("cancelled"); });
        Assert.AreEqual("unknown", result.State); Assert.AreEqual("unconfirmed", result.CancellationOutcome);
        Assert.AreEqual("submitting", (await repo.FindClientOrderOperationAsync(id))!.State);
        await Run(new(Repository()), id, (_, _) => { calls++; return Task.FromResult("cancelled"); }); Assert.AreEqual(1, calls);
    }
    [TestMethod]
    public async Task FailedClaimAndExpiredParentCannotStartCancellation()
    {
        var repo = Repository(); await repo.GetStatusAsync();
        await using var db = new SqliteConnection($"Data Source={Path.Combine(root, "test.db")};Pooling=False"); await db.OpenAsync();
        var sql = db.CreateCommand(); sql.CommandText = "CREATE TRIGGER reject_modify BEFORE INSERT ON trading_executions WHEN NEW.client_operation_kind='modify' BEGIN SELECT RAISE(ABORT,'synthetic'); END;"; await sql.ExecuteNonQueryAsync();
        var calls = 0; Task<string> Cancel(ClientModificationIds _, CancellationToken ct) { calls++; return Task.FromResult("cancelled"); }
        await Assert.ThrowsAsync<SqliteException>(() => Run(new(repo), Id(), Cancel));
        var expired = $"op1_{DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeSeconds()}_{Guid.NewGuid():N}";
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new(repo), expired, Cancel)); Assert.AreEqual(0, calls);
    }
    [TestMethod]
    public async Task GoneFailureAndLostReplyCannotBecomeAConfirmedCancellationOrPlacement()
    {
        foreach (var outcome in new[] { "gone_possibly_filled", "not_confirmed", "attention", "needs_acknowledgement", "not_resting", "resolved_cancelled", "private broker prose" }) {
            var result = await Run(new(Repository()), Id(), (_, _) => Task.FromResult(outcome));
            Assert.AreNotEqual("accepted", result.State); Assert.AreNotEqual("cancelled", result.CancellationOutcome);
        }
        var id = Id(); var lost = await Run(new(Repository()), id, (_, _) => throw new IOException("private diagnostic"));
        Assert.AreEqual("unknown", lost.State);
        Assert.AreEqual("unconfirmed", (await Run(new(Repository()), id, (_, _) => throw new AssertFailedException())).CancellationOutcome);
        Assert.IsFalse((await Repository().FindClientOrderOperationAsync(id))!.ResultJson!.Contains("private"));
    }
}
