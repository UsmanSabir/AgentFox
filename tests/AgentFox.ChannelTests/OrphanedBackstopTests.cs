using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Persistence;
using TradingAgent.Trading;
using TradingAgent.Watchlist;

namespace AgentFox.ChannelTests;

/// <summary>
/// A local backstop must not outlive the stop it backs.
///
/// <para>
/// Measured 2026-09-25, THCCL. A stop was superseded before any native order was placed for it.
/// <c>RetireSupersededAsync</c> closed the row with a bare state write and left its backstop armed, and
/// the monitor then logged "local backstop stood down" 41 times, long after the shares were sold,
/// because nothing ever retired the armed SELL. Two halves: the supersede close now takes the backstop
/// with it, and the monitor retires any backstop whose stop is already gone.
/// </para>
/// </summary>
[TestClass]
public sealed class OrphanedBackstopTests
{
    private string _root = "";

    [TestInitialize]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "agentfox-orphanbackstop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private SqliteTradingRepository NewRepository()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Workspaces:0"] = _root })
            .Build();

        return new SqliteTradingRepository(
            Options.Create(new TradingAgentOptions { DatabasePath = "trading/trading.db" }),
            configuration,
            NullLogger<SqliteTradingRepository>.Instance);
    }

    [TestMethod]
    public async Task Closing_a_superseded_stop_cancels_its_backstop()
    {
        var repository = NewRepository();
        await repository.SaveArmedOrderAsync(Backstop("backstop1", "stop1"));
        await repository.SaveProtectiveStopAsync(Stop("stop1", "superseded_pending_cancel", "backstop1"));

        var closed = await ProtectiveStopWorker.CloseSupersededAsync(
            repository, Stop("stop1", "superseded_pending_cancel", "backstop1"),
            "Superseded before any native order was placed for it; nothing to cancel.", default);

        Assert.IsTrue(closed);
        var stop = (await repository.GetProtectiveStopsAsync(openOnly: false)).Single(s => s.StopId == "stop1");
        Assert.AreEqual("closed", stop.State);
        var backstop = (await repository.GetArmedOrdersAsync(armedOnly: false)).Single(o => o.ArmedId == "backstop1");
        Assert.AreEqual("cancelled", backstop.State,
            "An armed backstop left behind a closed stop meets its trigger and stands down on every "
            + "pass until it expires; nothing else ever retires it.");
    }

    [TestMethod]
    public async Task A_supersede_close_that_lost_the_race_leaves_the_backstop_alone()
    {
        // Another path already moved the row on. That path owns what happens to the backstop, so this
        // one must not touch it on the strength of a close it did not make.
        var repository = NewRepository();
        await repository.SaveArmedOrderAsync(Backstop("backstop1", "stop1"));
        await repository.SaveProtectiveStopAsync(Stop("stop1", "active", "backstop1"));

        var closed = await ProtectiveStopWorker.CloseSupersededAsync(
            repository, Stop("stop1", "superseded_pending_cancel", "backstop1"), "reason", default);

        Assert.IsFalse(closed);
        var backstop = (await repository.GetArmedOrdersAsync(armedOnly: false)).Single(o => o.ArmedId == "backstop1");
        Assert.AreEqual("armed", backstop.State);
    }

    [TestMethod]
    public void A_backstop_is_orphaned_only_once_its_stop_is_closed_or_gone()
    {
        Assert.IsNotNull(ProtectiveStopDecisions.OrphanedBackstopReason(null));

        var closed = ProtectiveStopDecisions.OrphanedBackstopReason(
            Stop("stop1", "closed", "backstop1") with { StateReason = "Superseded." });
        StringAssert.Contains(closed, "Superseded.");

        // Every open state keeps its backstop, including the in-between one a supersede passes through:
        // until the old order is confirmed gone, the backstop may still be what covers the position.
        foreach (var open in new[] { "pending_fill", "active", "superseded_pending_cancel" })
            Assert.IsNull(ProtectiveStopDecisions.OrphanedBackstopReason(Stop("stop1", open, "backstop1")), open);
    }

    private static ProtectiveStop Stop(string stopId, string state, string? backstopId) => new()
    {
        StopId = stopId,
        Symbol = "THCCL",
        StopTrigger = 80m,
        StopLimit = 79.5m,
        DesiredQuantity = 57,
        Recurring = true,
        State = state,
        LocalBackstopArmedId = backstopId
    };

    private static ArmedOrder Backstop(string armedId, string stopId) => new()
    {
        ArmedId = armedId,
        Symbol = "THCCL",
        TriggerKind = ArmedTriggerKind.PriceBelow,
        TriggerPrice = 80m,
        Action = "SELL",
        Quantity = 57,
        OrderType = "LIMIT",
        Price = 79.5m,
        ProtectiveStopId = stopId,
        Note = "local backstop"
    };
}
