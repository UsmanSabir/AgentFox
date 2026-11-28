using AgentFox.Plugins.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using TradingAgent.Observability;

namespace AgentFox.ChannelTests;

/// <summary>Activity as the trader's "today": who each row is for, the PKT day, versions, and the "Notified" rows.</summary>
[TestClass]
public sealed class TradingActivityTodayTests
{
    // 14:00 PKT on 2026-11-28.
    private static readonly DateTime Noon = new(2026, 11, 28, 9, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void A_warning_or_an_error_is_always_for_the_trader()
    {
        Assert.AreEqual(ActivityAudience.Trades, TradingActivityLog.AudienceFor(ActivityLevel.Warn, "Broker", null));
        Assert.AreEqual(ActivityAudience.Trades, TradingActivityLog.AudienceFor(ActivityLevel.Error, "Feed", null));
    }

    [TestMethod]
    public void Routine_progress_without_a_stock_is_system()
    {
        Assert.AreEqual(ActivityAudience.System, TradingActivityLog.AudienceFor(ActivityLevel.Info, "Stops", null));
        Assert.AreEqual(ActivityAudience.System, TradingActivityLog.AudienceFor(ActivityLevel.Info, "Orders", null));
    }

    [TestMethod]
    public void A_stock_makes_it_a_trade_except_from_a_system_source()
    {
        Assert.AreEqual(ActivityAudience.Trades, TradingActivityLog.AudienceFor(ActivityLevel.Info, "Stops", "THCCL"));
        Assert.AreEqual(ActivityAudience.Trades, TradingActivityLog.AudienceFor(ActivityLevel.Info, "Armed", "MARI"));
        Assert.AreEqual(ActivityAudience.System, TradingActivityLog.AudienceFor(ActivityLevel.Info, "Broker", "MARI"));
        Assert.AreEqual(ActivityAudience.System, TradingActivityLog.AudienceFor(ActivityLevel.Info, "Feed", "MARI"));
    }

    [TestMethod]
    public void A_notification_is_for_the_trader()
    {
        Assert.AreEqual(ActivityAudience.Trades,
            TradingActivityLog.AudienceFor(ActivityLevel.Info, TradingActivityLog.NotifiedSource, null));
    }

    [TestMethod]
    public void Rows_carry_their_stock_and_audience()
    {
        var log = new TradingActivityLog(() => Noon);
        log.Info("Stops", "THCCL: stop placed", symbol: "thccl");
        log.Info("Stops", "Checking 3 protective stop(s)");

        var rows = log.Snapshot();
        Assert.AreEqual("THCCL", rows[1].Symbol);
        Assert.AreEqual("trades", rows[1].Audience);
        Assert.IsNull(rows[0].Symbol);
        Assert.AreEqual("system", rows[0].Audience);
    }

    [TestMethod]
    public void Prose_is_never_kept_as_a_stock()
    {
        var log = new TradingActivityLog(() => Noon);
        log.Info("Orders", "Something", symbol: "not a symbol");
        Assert.IsNull(log.Snapshot()[0].Symbol);
    }

    [TestMethod]
    public void The_day_is_the_PKT_calendar_day()
    {
        var now = new DateTime(2026, 11, 28, 18, 59, 0, DateTimeKind.Utc); // 23:59 PKT
        var log = new TradingActivityLog(() => now);
        log.Warn("Orders", "late order", symbol: "MARI");
        Assert.AreEqual(1, log.Snapshot().Count);
        Assert.AreEqual(new DateTime(2026, 11, 27, 19, 0, 0, DateTimeKind.Utc), log.KeptSinceUtc);

        now = now.AddMinutes(2); // 00:01 PKT on the 29th: a new day, read without any write
        Assert.AreEqual(0, log.Snapshot().Count);
        Assert.AreEqual((0, 0), log.IssueCounts());
        Assert.IsNull(log.OldestSeq);
    }

    [TestMethod]
    public void A_whole_trading_day_is_kept_not_two_hours()
    {
        var now = new DateTime(2026, 11, 28, 4, 0, 0, DateTimeKind.Utc); // 09:00 PKT
        var log = new TradingActivityLog(() => now);
        log.Info("Orders", "morning order", symbol: "MARI");
        now = now.AddHours(8);
        Assert.AreEqual(1, log.Snapshot().Count);
    }

    [TestMethod]
    public void A_repeat_bumps_the_version_so_a_reader_sees_it()
    {
        var log = new TradingActivityLog(() => Noon);
        log.Warn("Stops", "ATRL: stop refused", symbol: "ATRL");
        log.Info("Orders", "MARI: placed", symbol: "MARI");
        var seen = log.Version;
        Assert.AreEqual(0, log.Snapshot(afterVersion: seen).Count);

        log.Warn("Stops", "ATRL: stop refused", symbol: "ATRL");

        var changed = log.Snapshot(afterVersion: seen);
        Assert.AreEqual(1, changed.Count);
        Assert.AreEqual(1, changed[0].Repeats);
        Assert.AreEqual(log.Version, changed[0].Version);
    }

    [TestMethod]
    public void The_same_message_about_two_stocks_stays_two_rows()
    {
        var log = new TradingActivityLog(() => Noon);
        log.Info("Stops", "stop placed", symbol: "MARI");
        log.Info("Stops", "stop placed", symbol: "OGDC");
        Assert.AreEqual(2, log.Snapshot().Count);
    }

    [TestMethod]
    public async Task A_channel_message_is_recorded_with_its_delivery()
    {
        var log = new TradingActivityLog(() => Noon);
        var notifier = new ActivityRecordingNotifier(new FixedNotifier(2), log);

        var sent = await notifier.NotifyAsync("*BUY 57 THCCL filled*\nAt 84.49", "trading.order.filled");

        Assert.AreEqual(2, sent);
        var row = log.Snapshot().Single();
        Assert.AreEqual(TradingActivityLog.NotifiedSource, row.Source);
        Assert.AreEqual("BUY 57 THCCL filled", row.Message);
        Assert.AreEqual("Sent to 2 channels · At 84.49", row.Detail);
        Assert.AreEqual("trades", row.Audience);
    }

    [TestMethod]
    public async Task A_message_no_channel_took_says_so()
    {
        var log = new TradingActivityLog(() => Noon);
        await new ActivityRecordingNotifier(new FixedNotifier(0), log).NotifyAsync("Price alert: KSE 100");
        Assert.AreEqual("No channel accepted it", log.Snapshot().Single().Detail);
    }

    [TestMethod]
    public async Task A_failed_send_is_recorded_and_still_thrown()
    {
        var log = new TradingActivityLog(() => Noon);
        var notifier = new ActivityRecordingNotifier(new ThrowingNotifier(), log);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => notifier.NotifyAsync("Stop fired"));

        var row = log.Snapshot().Single();
        Assert.AreEqual("warn", row.Level);
        StringAssert.StartsWith(row.Detail, "Not delivered: channel down");
    }

    [TestMethod]
    public void Decorating_wraps_the_host_notifier_once()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TradingActivityLog>();
        services.AddSingleton<IUserNotifier>(new FixedNotifier(1));

        ActivityRecordingNotifier.Decorate(services);
        ActivityRecordingNotifier.Decorate(services);

        using var provider = services.BuildServiceProvider();
        Assert.IsInstanceOfType<ActivityRecordingNotifier>(provider.GetRequiredService<IUserNotifier>());
        Assert.AreEqual(1, services.Count(d => d.ServiceType == typeof(IUserNotifier)));
    }

    [TestMethod]
    public void Without_a_host_notifier_nothing_is_registered()
    {
        var services = new ServiceCollection();
        ActivityRecordingNotifier.Decorate(services);
        Assert.AreEqual(0, services.Count);
    }

    private sealed class FixedNotifier(int count) : IUserNotifier
    {
        public Task<int> NotifyAsync(string message, CancellationToken ct = default) => Task.FromResult(count);
        public Task<int> NotifyAsync(string message, string? topic, CancellationToken ct = default) => Task.FromResult(count);
    }

    private sealed class ThrowingNotifier : IUserNotifier
    {
        public Task<int> NotifyAsync(string message, CancellationToken ct = default) => throw new InvalidOperationException("channel down");
        public Task<int> NotifyAsync(string message, string? topic, CancellationToken ct = default) => throw new InvalidOperationException("channel down");
    }
}
