using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Market;

namespace AgentFox.ChannelTests;

[TestClass]
public sealed class PsxMarketCalendarTests
{
    private static PsxMarketCalendar Calendar(
        IEnumerable<string>? holidays = null,
        IEnumerable<MarketSessionOverride>? overrides = null) =>
        new(
            Options.Create(new TradingAgentOptions
            {
                MarketHolidays = holidays?.ToList() ?? [],
                MarketSessionOverrides = overrides?.ToList() ?? []
            }),
            NullLogger<PsxMarketCalendar>.Instance);

    private static DateTime Utc(int year, int month, int day, int hour, int minute) =>
        TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified),
            PsxTime.Zone);

    [TestMethod]
    public void BeforeARegularSession_ReportsTodaysOpening()
    {
        var status = Calendar().GetStatus(Utc(2026, 8, 26, 9, 0)); // Wednesday

        Assert.IsFalse(status.IsOpen);
        Assert.AreEqual(new DateTime(2026, 8, 26, 9, 32, 0), status.NextOpenPkt);
    }

    [TestMethod]
    public void AfterTheFinalBell_ProjectsTheNextTradingDate()
    {
        var status = Calendar().GetStatus(Utc(2026, 8, 27, 16, 0)); // Thursday

        Assert.IsFalse(status.IsOpen);
        Assert.AreEqual(new DateTime(2026, 8, 28, 9, 17, 0), status.NextOpenPkt);
    }

    [TestMethod]
    public void FridayLunchBreak_ReportsTheSecondSameDayOpening()
    {
        var status = Calendar().GetStatus(Utc(2026, 8, 28, 13, 0));

        Assert.IsFalse(status.IsOpen);
        Assert.AreEqual(new DateTime(2026, 8, 28, 14, 32, 0), status.NextOpenPkt);
    }

    [TestMethod]
    public void FridayClose_SkipsWeekendAndConfiguredMondayHoliday()
    {
        var status = Calendar(["2026-08-31"]).GetStatus(Utc(2026, 8, 28, 17, 0));

        Assert.IsFalse(status.IsOpen);
        Assert.AreEqual(new DateTime(2026, 9, 1, 9, 32, 0), status.NextOpenPkt);
    }

    [TestMethod]
    public void ClosedOverride_ProjectsTheFollowingConfiguredSession()
    {
        var status = Calendar(overrides:
        [
            new MarketSessionOverride { Date = "2026-08-26", Closed = true },
            new MarketSessionOverride
            {
                Date = "2026-08-27",
                Sessions = ["10:15-12:30"]
            }
        ]).GetStatus(Utc(2026, 8, 26, 9, 0));

        Assert.IsFalse(status.IsOpen);
        Assert.AreEqual(new DateTime(2026, 8, 27, 10, 15, 0), status.NextOpenPkt);
        Assert.AreEqual("override", status.ScheduleSource);
    }
}
