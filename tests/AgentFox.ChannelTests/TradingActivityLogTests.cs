using TradingAgent.Observability;

namespace AgentFox.ChannelTests;

/// <summary>
/// The activity log behind the trading dashboard's activity panel.
///
/// <para>
/// Its whole value is being short enough to read at a glance, and everything written to it comes from
/// pollers — the protective-stop pass alone posts the same handful of lines every few minutes. So the
/// rules under test are the ones that keep a poller from burying a one-off event: identical
/// activities collapse into a count rather than a row each, and a collapsed line keeps its original
/// position rather than being promoted every time it recurs.
/// </para>
/// </summary>
[TestClass]
public sealed class TradingActivityLogTests
{
    [TestMethod]
    public void Repeats_CollapseIntoOneEntryWithACount()
    {
        var log = new TradingActivityLog();

        log.Info("Broker", "Browser session opened");
        log.Info("Broker", "Browser session opened");
        log.Info("Broker", "Browser session opened");

        var entries = log.Snapshot();

        Assert.AreEqual(1, entries.Count, "Three identical activities must not occupy three rows.");
        Assert.AreEqual(2, entries[0].Repeats, "Repeats counts the FURTHER occurrences, not the first.");
        Assert.IsTrue(entries[0].LastUtc >= entries[0].Utc);
    }

    [TestMethod]
    public void ActivitiesThatDifferInAnyPartAreSeparateEntries()
    {
        var log = new TradingActivityLog();

        log.Info("Broker", "Reading the outstanding order book");
        log.Info("Stops", "Reading the outstanding order book");          // different source
        log.Warn("Broker", "Reading the outstanding order book");          // different level
        log.Info("Broker", "Reading the outstanding order book", "for X"); // different detail

        Assert.AreEqual(4, log.Snapshot().Count);
    }

    [TestMethod]
    public void ACollapsedRepeatDoesNotPromoteItselfAboveNewerEvents()
    {
        // The failure this prevents: a poll line recurring every few minutes would sit permanently at
        // the top of the panel, pushing the one-off events it exists to surface out of view.
        var log = new TradingActivityLog();

        log.Info("Broker", "Browser session opened");
        log.Error("Stops", "FFC: the stop was NOT placed");
        log.Info("Broker", "Browser session opened");

        var entries = log.Snapshot();

        Assert.AreEqual(2, entries.Count);
        Assert.AreEqual("FFC: the stop was NOT placed", entries[0].Message, "Newest first.");
        Assert.AreEqual(1, entries[1].Repeats);
    }

    [TestMethod]
    public void IssueCountsSeparateWarningsFromErrors()
    {
        var log = new TradingActivityLog();

        log.Info("Broker", "Portfolio read: 3 holding(s)");
        log.Warn("Feed", "Could not establish a broker session");
        log.Error("Orders", "BUY FFC was NOT placed");
        log.Error("Orders", "SELL OGDC was NOT placed");

        var (warnings, errors) = log.IssueCounts();

        Assert.AreEqual(1, warnings);
        Assert.AreEqual(2, errors);
    }

    [TestMethod]
    public void TheWindowIsCappedSoAPollerCannotGrowItWithoutBound()
    {
        var log = new TradingActivityLog();

        // Distinct messages, so none of them collapse — this is the worst case for growth.
        for (var i = 0; i < TradingActivityLog.Capacity * 2; i++)
            log.Info("Monitor", $"Monitoring pass raised {i} alert(s)");

        var entries = log.Snapshot();

        Assert.AreEqual(TradingActivityLog.Capacity, entries.Count);
        Assert.AreEqual(
            $"Monitoring pass raised {TradingActivityLog.Capacity * 2 - 1} alert(s)",
            entries[0].Message,
            "Capacity is enforced by dropping the OLDEST, never the newest.");
    }

    [TestMethod]
    public void AfterSeqReturnsOnlyWhatTheCallerHasNotSeen()
    {
        var log = new TradingActivityLog();

        log.Info("Broker", "one");
        var seen = log.LastSeq;
        log.Info("Broker", "two");

        var entries = log.Snapshot(afterSeq: seen);

        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual("two", entries[0].Message);
    }

    [TestMethod]
    public void AnEmptyMessageIsNotRecorded()
    {
        var log = new TradingActivityLog();

        log.Info("Broker", "   ");

        Assert.AreEqual(0, log.Snapshot().Count);
        Assert.AreEqual(0, log.LastSeq);
    }
}
