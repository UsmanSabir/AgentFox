using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Persistence;
using TradingAgent.Watchlist;

namespace AgentFox.ChannelTests;

/// <summary>
/// Stops only move up unless someone authorised otherwise, and a stop has one open successor at a time — both checked
/// inside the repository's save, so no writer can bypass them and no two writers can race past them (plan step 4).
/// </summary>
[TestClass]
public sealed class StopUpOnlyGuardTests
{
    private string _root = "";

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), "agentfox-uponly-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    /// <summary>A separate instance per writer, each with its own connections, like two hosted workers.</summary>
    private SqliteTradingRepository Repo() => new(
        Options.Create(new TradingAgentOptions { DatabasePath = Path.Combine(_root, "t.db") }),
        new ConfigurationBuilder().Build(), NullLogger<SqliteTradingRepository>.Instance);

    private static ProtectiveStop Stop(string id, decimal trigger, string? supersedes = null, string? loweredBy = null) => new()
    {
        StopId = id, Symbol = "PPL", StopTrigger = trigger, StopLimit = Math.Round(trigger * 0.99m, 2),
        DesiredQuantity = 100, State = "active", SupersedesStopId = supersedes, LoweringAuthorizedBy = loweredBy
    };

    [TestMethod]
    public async Task ARaiseIsSaved()
    {
        var repo = Repo();
        await repo.SaveProtectiveStopAsync(Stop("A", 100m));

        await repo.SaveProtectiveStopAsync(Stop("B", 105m, supersedes: "A"));

        Assert.AreEqual(2, (await repo.GetProtectiveStopsAsync()).Count);
    }

    [TestMethod]
    public async Task ALowerTriggerNobodyAuthorisedIsRefused_AndThePredecessorStands()
    {
        var repo = Repo();
        await repo.SaveProtectiveStopAsync(Stop("A", 100m));

        var refused = await Assert.ThrowsAsync<ProtectiveStopLoweringRefusedException>(
            () => repo.SaveProtectiveStopAsync(Stop("B", 95m, supersedes: "A")));

        StringAssert.Contains(refused.Message, "BELOW");
        Assert.AreEqual("A", (await repo.GetProtectiveStopsAsync()).Single().StopId);
    }

    [TestMethod]
    public async Task AnOperatorMayLowerAStopWithAnAuthorisation_AndItIsRecorded()
    {
        var repo = Repo();
        await repo.SaveProtectiveStopAsync(Stop("A", 100m));

        await repo.SaveProtectiveStopAsync(Stop("B", 95m, supersedes: "A", loweredBy: "owner, confirmed in the stop editor"));

        var saved = (await repo.GetProtectiveStopsAsync()).Single(s => s.StopId == "B");
        Assert.AreEqual(95m, saved.StopTrigger);
        Assert.AreEqual("owner, confirmed in the stop editor", saved.LoweringAuthorizedBy, "the audit row");
    }

    [TestMethod]
    public async Task ASameTriggerReplacementASizeChangeIsSaved()
    {
        var repo = Repo();
        await repo.SaveProtectiveStopAsync(Stop("A", 100m));

        await repo.SaveProtectiveStopAsync(Stop("B", 100m, supersedes: "A") with { DesiredQuantity = 40 });

        Assert.AreEqual(2, (await repo.GetProtectiveStopsAsync()).Count);
    }

    [TestMethod]
    public async Task AClosedPredecessorImposesNoFloor()
    {
        var repo = Repo();
        await repo.SaveProtectiveStopAsync(Stop("A", 100m) with { State = "closed" });

        await repo.SaveProtectiveStopAsync(Stop("B", 90m, supersedes: "A"));

        Assert.IsNotNull((await repo.GetProtectiveStopsAsync(openOnly: false)).SingleOrDefault(s => s.StopId == "B"));
    }

    [TestMethod]
    public async Task AStopHasOneOpenSuccessorAtATime()
    {
        var repo = Repo();
        await repo.SaveProtectiveStopAsync(Stop("A", 100m));
        await repo.SaveProtectiveStopAsync(Stop("B", 105m, supersedes: "A"));

        await Assert.ThrowsAsync<ProtectiveStopSupersedeConflictException>(
            () => repo.SaveProtectiveStopAsync(Stop("C", 106m, supersedes: "A")));
    }

    /// <summary>
    /// The race the owner asked for. The overnight ATR trail raises A (100) to B (105). A strategy that read the
    /// position before that write then raises to 102 — below what now stands. Whichever predecessor it names, it is
    /// refused: A already has an open successor, and B is higher than 102. The stop stays at 105.
    /// </summary>
    [TestMethod]
    public async Task TheOvernightTrailAndAStrategyRaiseCannotLowerAStopBetweenThem()
    {
        var trail = Repo();
        var strategy = Repo();
        await trail.SaveProtectiveStopAsync(Stop("A", 100m));

        await trail.SaveProtectiveStopAsync(Stop("B", 105m, supersedes: "A"));   // the trail, overnight

        // The strategy's view was taken before the trail wrote: it still thinks A is the stop to replace.
        await Assert.ThrowsAsync<ProtectiveStopSupersedeConflictException>(
            () => strategy.SaveProtectiveStopAsync(Stop("C", 102m, supersedes: "A")));
        // Re-read, it names B — and 102 is below B.
        await Assert.ThrowsAsync<ProtectiveStopLoweringRefusedException>(
            () => strategy.SaveProtectiveStopAsync(Stop("C", 102m, supersedes: "B")));

        var open = await strategy.GetProtectiveStopsAsync();
        Assert.AreEqual(105m, open.Single(s => !open.Any(o => o.SupersedesStopId == s.StopId)).StopTrigger);
    }

    [TestMethod]
    public async Task TwoWritersReplacingTheSameStopAtOnceProduceExactlyOneSuccessor()
    {
        await Repo().SaveProtectiveStopAsync(Stop("A", 100m));

        var attempts = Enumerable.Range(0, 2).Select(i => Task.Run(async () =>
        {
            try { await Repo().SaveProtectiveStopAsync(Stop($"S{i}", 104m + i, supersedes: "A")); return true; }
            catch (ProtectiveStopSupersedeConflictException) { return false; }
        })).ToArray();
        var outcomes = await Task.WhenAll(attempts);

        Assert.AreEqual(1, outcomes.Count(ok => ok));
        Assert.AreEqual(1, (await Repo().GetProtectiveStopsAsync()).Count(s => s.SupersedesStopId == "A"));
    }
}
