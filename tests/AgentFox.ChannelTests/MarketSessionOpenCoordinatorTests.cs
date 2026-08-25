using Microsoft.Extensions.Logging.Abstractions;
using TradingAgent.Market;

namespace AgentFox.ChannelTests;

[TestClass]
public sealed class MarketSessionOpenCoordinatorTests
{
    [TestMethod]
    public async Task Participants_RunInSafetyOrder_ExactlyOncePerSession()
    {
        var calls = new List<string>();
        var calendar = new MutableCalendar(OpenAt(new DateTime(2026, 8, 26, 9, 32, 1)));
        var coordinator = Coordinator(calendar,
        [
            new RecordingParticipant("signals", 400, calls),
            new RecordingParticipant("reconciliation", 100, calls),
            new RecordingParticipant("orders", 300, calls),
            new RecordingParticipant("protection", 200, calls)
        ]);

        Assert.IsTrue(await coordinator.TryRunOpenSessionAsync());
        Assert.IsFalse(await coordinator.TryRunOpenSessionAsync());
        CollectionAssert.AreEqual(
            new[] { "reconciliation", "protection", "orders", "signals" }, calls);
    }

    [TestMethod]
    public async Task AFailedParticipant_DoesNotPreventLaterSafetySteps()
    {
        var calls = new List<string>();
        var calendar = new MutableCalendar(OpenAt(new DateTime(2026, 8, 26, 9, 32, 1)));
        var coordinator = Coordinator(calendar,
        [
            new RecordingParticipant("first", 100, calls, throws: true),
            new RecordingParticipant("second", 200, calls)
        ]);

        Assert.IsTrue(await coordinator.TryRunOpenSessionAsync());
        CollectionAssert.AreEqual(new[] { "first", "second" }, calls);
    }

    [TestMethod]
    public async Task FridayAfternoon_IsASecondDistinctSession()
    {
        var calls = new List<string>();
        var calendar = new MutableCalendar(OpenAt(new DateTime(2026, 8, 28, 9, 17, 1), 9, 17));
        var coordinator = Coordinator(calendar, [new RecordingParticipant("worker", 100, calls)]);

        Assert.IsTrue(await coordinator.TryRunOpenSessionAsync());
        calendar.Status = OpenAt(new DateTime(2026, 8, 28, 14, 32, 1), 14, 32);
        Assert.IsTrue(await coordinator.TryRunOpenSessionAsync());

        CollectionAssert.AreEqual(new[] { "worker", "worker" }, calls);
    }

    [TestMethod]
    public async Task ClosedMarket_DoesNotRunParticipants()
    {
        var calls = new List<string>();
        var calendar = new MutableCalendar(new MarketStatus(
            false,
            new DateTime(2026, 8, 26, 9, 0, 0),
            "closed",
            new DateTime(2026, 8, 26, 9, 32, 0)));
        var coordinator = Coordinator(calendar, [new RecordingParticipant("worker", 100, calls)]);

        Assert.IsFalse(await coordinator.TryRunOpenSessionAsync());
        Assert.AreEqual(0, calls.Count);
    }

    private static MarketSessionOpenCoordinator Coordinator(
        IMarketCalendar calendar,
        IEnumerable<IMarketSessionOpenParticipant> participants) =>
        new(
            calendar,
            participants,
            TimeProvider.System,
            NullLogger<MarketSessionOpenCoordinator>.Instance);

    private static MarketStatus OpenAt(DateTime now, int hour = 9, int minute = 32) =>
        new(
            true,
            now,
            "open",
            ScheduleSource: "test",
            SessionOpenPkt: new DateTime(now.Year, now.Month, now.Day, hour, minute, 0));

    private sealed class MutableCalendar(MarketStatus status) : IMarketCalendar
    {
        public MarketStatus Status { get; set; } = status;
        public MarketStatus GetStatus(DateTime? utcNow = null) => Status;
    }

    private sealed class RecordingParticipant(
        string name,
        int order,
        List<string> calls,
        bool throws = false) : IMarketSessionOpenParticipant
    {
        public string Name => name;
        public int Order => order;

        public Task RunAtMarketOpenAsync(MarketSessionOpenContext context, CancellationToken ct)
        {
            calls.Add(name);
            return throws
                ? Task.FromException(new InvalidOperationException("expected test failure"))
                : Task.CompletedTask;
        }
    }
}
