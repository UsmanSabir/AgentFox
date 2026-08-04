using AgentFox.Agents;
using AgentFox.Channels;
using AgentFox.Plugins.Channels;
using AgentFox.Sessions;
using AgentFox.Tools;

namespace AgentFox.ChannelTests;

/// <summary>
/// <c>notify_user</c> is the only route to the user's Discord/Telegram channels, and it was
/// reachable twice for one piece of work:
///
///  - an auto-continuation (or a todo item still reading "share the update" because the wrong id
///    was completed) sent the model straight back to it with the report it had just delivered;
///  - the tool lives in the single shared registry, so a sub-agent inherits it — a parent that
///    delegated "gather the data and post the update" produced one delivery from the sub-agent and
///    another from its own turn.
///
/// Both showed up in production as the same summary arriving two, three, four times.
/// </summary>
[TestClass]
public sealed class NotifyUserDuplicateTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "agentfox_notify_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        FoxAgent.CurrentSessionKey.Value = null;
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── Duplicate suppression ─────────────────────────────────────────────────

    [TestMethod]
    public async Task IdenticalMessage_IsDeliveredOncePerSession()
    {
        var (tool, channel) = NewTool();
        FoxAgent.CurrentSessionKey.Value = "cron_psx-daily-summary_20260804_060054";

        const string report = "PSX Daily Market Update. KSE-100 closed at 178,200.02, up 1.20%.";

        var first = await tool.ExecuteAsync(new() { ["message"] = report });
        var second = await tool.ExecuteAsync(new() { ["message"] = report });

        Assert.IsTrue(first.Success);
        Assert.IsTrue(second.Success, "A suppressed resend must not look like a failure to retry.");
        Assert.AreEqual(1, channel.Sent.Count,
            "The same report reached the user twice — this is the duplicate the user saw on Discord.");
        StringAssert.Contains(second.Output, "Already delivered");
    }

    [TestMethod]
    public async Task ReportWithOnlyFiguresChanged_IsTreatedAsTheSameMessage()
    {
        var (tool, channel) = NewTool();
        FoxAgent.CurrentSessionKey.Value = "cron_psx-daily-summary_20260804_060054";

        // The real resend was not byte-identical: a couple of numbers and the timestamp moved.
        var first = Report(close: "178,200.02", retrieved: "11:02 AM PKT");
        var second = Report(close: "178,278.94", retrieved: "11:15 AM PKT");

        await tool.ExecuteAsync(new() { ["message"] = first });
        await tool.ExecuteAsync(new() { ["message"] = second });

        Assert.AreEqual(1, channel.Sent.Count,
            "A re-delivery of the same report with refreshed figures is still a duplicate.");
    }

    [TestMethod]
    public async Task ShortMessageWithDifferentFigures_IsNotSuppressed()
    {
        // Documents the deliberate limit of the guard. Suppression strength is proportional to
        // message length: a handful of changed tokens barely dents the shingle set of a long
        // report, but dominates a one-liner. That asymmetry is wanted — long reports are the ones
        // that hurt when duplicated, while short status lines are often legitimately similar and
        // must not be swallowed.
        var (tool, channel) = NewTool();
        FoxAgent.CurrentSessionKey.Value = "alerts";

        await tool.ExecuteAsync(new() { ["message"] = "Price alert: MARI crossed 655.98." });
        await tool.ExecuteAsync(new() { ["message"] = "Price alert: MARI crossed 700.10." });

        Assert.AreEqual(2, channel.Sent.Count,
            "Two short alerts differing in their figures are different messages, not a resend.");
    }

    [TestMethod]
    public async Task GenuinelyDifferentMessage_StillGoesOut()
    {
        var (tool, channel) = NewTool();
        FoxAgent.CurrentSessionKey.Value = "session-a";

        await tool.ExecuteAsync(new() { ["message"] = Report("178,200.02", "11:02 AM PKT") });
        var other = await tool.ExecuteAsync(new()
        {
            ["message"] = "Heads up: the broker portal is rejecting logins, so no portfolio data today."
        });

        Assert.IsTrue(other.Success);
        Assert.AreEqual(2, channel.Sent.Count,
            "Suppression must not swallow an unrelated message sent in the same session.");
    }

    [TestMethod]
    public async Task SuppressionIsScopedToOneSession()
    {
        var (tool, channel) = NewTool();
        const string report = "PSX Daily Market Update. KSE-100 closed at 178,200.02, up 1.20%.";

        FoxAgent.CurrentSessionKey.Value = "cron_psx-daily-summary_20260804_060054";
        await tool.ExecuteAsync(new() { ["message"] = report });

        // Tomorrow's run is a different session and legitimately sends its own update.
        FoxAgent.CurrentSessionKey.Value = "cron_psx-daily-summary_20260805_060054";
        await tool.ExecuteAsync(new() { ["message"] = report });

        Assert.AreEqual(2, channel.Sent.Count,
            "A later run is a separate session and must be able to send its own update.");
    }

    [TestMethod]
    public async Task SuppressionExpiresAfterTheWindow()
    {
        var (tool, channel) = NewTool(duplicateWindow: TimeSpan.FromMilliseconds(120));
        FoxAgent.CurrentSessionKey.Value = "short-window";

        const string report = "PSX Daily Market Update. KSE-100 closed at 178,200.02, up 1.20%.";

        await tool.ExecuteAsync(new() { ["message"] = report });
        await Task.Delay(250);
        await tool.ExecuteAsync(new() { ["message"] = report });

        Assert.AreEqual(2, channel.Sent.Count,
            "Suppression is a short guard against resends, not a permanent block on the content.");
    }

    [TestMethod]
    public async Task FailedDeliveryStaysRetryable()
    {
        var channel = new FakeChannel("discord", failSends: true);
        var tool = NewTool(channel).tool;
        FoxAgent.CurrentSessionKey.Value = "failing";

        const string report = "PSX Daily Market Update.";

        var first = await tool.ExecuteAsync(new() { ["message"] = report });
        Assert.IsFalse(first.Success);

        channel.FailSends = false;
        var retry = await tool.ExecuteAsync(new() { ["message"] = report });

        Assert.IsTrue(retry.Success,
            "A message that never reached anyone must not be recorded as already delivered.");
        Assert.AreEqual(1, channel.Sent.Count);
    }

    [TestMethod]
    public async Task ZeroWindowDisablesSuppression()
    {
        var (tool, channel) = NewTool(duplicateWindow: TimeSpan.Zero);
        FoxAgent.CurrentSessionKey.Value = "no-dedupe";

        const string report = "PSX Daily Market Update.";
        await tool.ExecuteAsync(new() { ["message"] = report });
        await tool.ExecuteAsync(new() { ["message"] = report });

        Assert.AreEqual(2, channel.Sent.Count, "A zero window is the documented opt-out.");
    }

    // ── Sub-agents ────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task SubAgentSession_CannotDeliverToChannels()
    {
        using var sessions = CreateSessionManager();
        var subAgentSession = sessions.CreateSubAgentSession(
            agentId: "PSX-Market-Data", runId: Guid.NewGuid().ToString("N"), parentSessionId: "parent");

        var (tool, channel) = NewTool(sessionManager: sessions);
        FoxAgent.CurrentSessionKey.Value = subAgentSession;

        var result = await tool.ExecuteAsync(new() { ["message"] = "Here is the PSX update." });

        Assert.IsFalse(result.Success);
        Assert.AreEqual(0, channel.Sent.Count,
            "A sub-agent delivering directly is how one run produced two Discord messages.");
        StringAssert.Contains(result.Output + result.Error, "Sub-agents cannot deliver");
    }

    [TestMethod]
    public async Task SubAgentSession_IsRecognisedWithoutASessionIndex()
    {
        // Embedded hosts and tests may have no session index; fall back to the id shape
        // CreateSubAgentSession produces ("{agentId}/sa_{runId}").
        var (tool, channel) = NewTool();
        FoxAgent.CurrentSessionKey.Value = "PSX-Market-Data/sa_68160904f8044ae6ad33077f81eaaf81";

        var result = await tool.ExecuteAsync(new() { ["message"] = "Here is the PSX update." });

        Assert.IsFalse(result.Success);
        Assert.AreEqual(0, channel.Sent.Count);
    }

    [TestMethod]
    public async Task SubAgentSendsCanBeOptedBackIn()
    {
        using var sessions = CreateSessionManager();
        var subAgentSession = sessions.CreateSubAgentSession(
            "PSX-Market-Data", Guid.NewGuid().ToString("N"), "parent");

        var (tool, channel) = NewTool(sessionManager: sessions, allowSubAgentSends: true);
        FoxAgent.CurrentSessionKey.Value = subAgentSession;

        var result = await tool.ExecuteAsync(new() { ["message"] = "Here is the PSX update." });

        Assert.IsTrue(result.Success, "Tools:SubAgentNotify must re-enable direct delivery.");
        Assert.AreEqual(1, channel.Sent.Count);
    }

    [TestMethod]
    public async Task CronSession_DeliversNormally()
    {
        using var sessions = CreateSessionManager();
        var cronSession = sessions.CreateFreshSession(
            SessionOrigin.CronJob, "psx-daily-summary", "main");

        var (tool, channel) = NewTool(sessionManager: sessions);
        FoxAgent.CurrentSessionKey.Value = cronSession;

        var result = await tool.ExecuteAsync(new() { ["message"] = "Here is the PSX update." });

        Assert.IsTrue(result.Success, "The sub-agent block must not catch a cron run.");
        Assert.AreEqual(1, channel.Sent.Count);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A stand-in for the real payload: a full five-section markdown report, of the order of
    /// length the cron job actually posted. Size matters to what is being tested — see
    /// <see cref="ShortMessageWithDifferentFigures_IsNotSuppressed"/>.
    /// </summary>
    private static string Report(string close, string retrieved) =>
        "# 📊 PSX Daily Market Update — Tuesday, 4 August 2026\n\n"
        + "## 1. Market Status & Trend\n\n"
        + "### KSE-100 Index\n"
        + "| Metric | Value |\n"
        + "|--------|-------|\n"
        + $"| Latest Close | {close} |\n"
        + "| Day Change | +2,105.91 (+1.20%) |\n"
        + "| Previous Close | 176,094.11 |\n"
        + "| Day High / Low | 178,985.94 / 177,759.88 |\n"
        + "| Volume | 255.20M shares (KSE-100) / 785.36M (All-Share) |\n"
        + "| Value Traded | PKR 33.14B |\n\n"
        + "### Trend Assessment\n"
        + "- Short-term (daily): strong recovery. The index surged on the strongest daily gain in\n"
        + "  weeks, with broad-based buying across banks, cement, fertilizer, and oil and gas.\n"
        + "- Medium-term (monthly): turning bullish. The rally has reversed the recent monthly\n"
        + "  downtrend and the index is reclaiming ground lost in late July.\n"
        + "- Key level: a sustained hold above 178,000 on continued volume would confirm the\n"
        + "  bullish shift. The next target after that is 180,000.\n\n"
        + "### Macro Backdrop\n"
        + "- Inflation: 9.2% in July, down from 11.10% in June, back to single digits for the\n"
        + "  first time in months.\n"
        + "- Interest rate: 11.50%, left unchanged at the most recent meeting.\n"
        + "- US-Pakistan tariffs: finalized at 19%, revised down from the threatened 29%.\n"
        + "- Geopolitical: US-Iran talks resumed, easing regional risk sentiment.\n"
        + "- Rupee: 210th consecutive session of gains against the dollar.\n"
        + "- Reserves: 22.67B dollars in total across the central bank and commercial banks.\n\n"
        + "### Breadth\n"
        + "- KSE-100: 79 advanced, 20 declined, 1 unchanged — overwhelmingly positive.\n"
        + "- All-Share Index: 305 advanced, 152 declined, 36 unchanged — broad-based strength.\n\n"
        + "## 2. Portfolio Context\n\n"
        + "The broker portal requires authentication, so live portfolio data is not retrievable.\n"
        + "The previous session's holdings are used as the baseline below.\n\n"
        + "| Symbol | Shares | Avg Buy | Current | P&L % |\n"
        + "|--------|--------|---------|---------|-------|\n"
        + "| MARI | 125 | 647.56 | 655.98 | +1.30% |\n"
        + "| MEBL | 80 | 545.19 | 567.68 | +4.13% |\n"
        + "| NML | 175 | 151.27 | 148.83 | -1.61% |\n"
        + "| OGDC | 150 | 324.61 | 318.98 | -1.73% |\n"
        + "| PAEL | 10 | 44.68 | 42.82 | -4.16% |\n"
        + "| PPL | 300 | 237.05 | 222.10 | -6.31% |\n"
        + "| SELECT | 600 | 32.16 | 28.35 | -11.85% |\n"
        + "| SLM | 350 | 24.53 | 24.21 | -1.30% |\n\n"
        + "Available cash stands at PKR 27,006.00 against total invested of PKR 299,167.25.\n"
        + "The weakest positions are SELECT, PPL and PAEL; the strongest are MEBL and MARI.\n\n"
        + "## 3. Investment Recommendations\n\n"
        + "- HOLD MEBL and MARI. They are the best performers and there is no reason to exit\n"
        + "  quality holdings while the market is recovering.\n"
        + "- HOLD NML, OGDC and SLM. The losses are moderate and selling into a recovering trend\n"
        + "  would only lock them in.\n"
        + "- CONSIDER TRIMMING PPL and SELECT, the two largest losers, if bearish momentum\n"
        + "  returns. The strong session just past suggests the worst may already be over.\n"
        + "- AVOID aggressive new buys today. Wait for the session to confirm the trend holds\n"
        + "  while the market consolidates its gains.\n"
        + "- HOLD the cash reserve. Volatility is elevated and cash provides optionality.\n\n"
        + "## 4. Suggested Action Sequence\n\n"
        + "1. Hold existing positions; the rally is a strong positive signal and the worst of the\n"
        + "   selling pressure appears to have passed.\n"
        + "2. Review the PPL and SELECT positions, which are most exposed to further downside.\n"
        + "3. Keep the quality positions, which are performing well and should be maintained.\n"
        + "4. Wait for confirmation above the key level before considering new aggressive buys.\n"
        + "5. Monitor the macro drivers: talks, tariff implementation, inflation and oil prices.\n"
        + "6. Reassess at the next session, since intraday developments could shift the trend.\n\n"
        + "## 5. Important Caveats\n\n"
        + "- This is not financial advice. The exchange is highly sensitive to geopolitical and\n"
        + "  macroeconomic shifts and prices can change rapidly.\n"
        + "- Portfolio data is based on the previous session because the broker portal requires\n"
        + "  authentication, so actual current prices may differ.\n"
        + "- Forecast models still project a lower index over a twelve-month horizon; bearish\n"
        + "  expectations persist even though the latest session contradicts them.\n\n"
        + $"*Sources: exchange market summary, macro data providers. Retrieved {retrieved}.*";

    private (NotifyUserTool tool, FakeChannel channel) NewTool(
        SessionManager? sessionManager = null,
        bool allowSubAgentSends = false,
        TimeSpan? duplicateWindow = null)
        => NewTool(new FakeChannel("discord"), sessionManager, allowSubAgentSends, duplicateWindow);

    private (NotifyUserTool tool, FakeChannel channel) NewTool(
        FakeChannel channel,
        SessionManager? sessionManager = null,
        bool allowSubAgentSends = false,
        TimeSpan? duplicateWindow = null)
    {
        var manager = new ChannelManager(() => null);
        manager.AddChannel(channel);

        var tool = new NotifyUserTool(
            manager,
            logger: null,
            sessionManager: sessionManager,
            allowSubAgentSends: allowSubAgentSends,
            duplicateWindow: duplicateWindow ?? TimeSpan.FromMinutes(5));

        return (tool, channel);
    }

    private SessionManager CreateSessionManager()
    {
        var workspace = new WorkspaceManager([_root], restrictToWorkspace: false);
        return new SessionManager(new SessionConfig
        {
            SessionDirectory = "sessions",
            ArchiveDirectory = "archive/sessions",
            BackgroundCheckIntervalSeconds = 3600
        }, workspace);
    }

    private sealed class FakeChannel : Channel
    {
        public List<string> Sent { get; } = [];
        public bool FailSends { get; set; }

        public FakeChannel(string type, bool failSends = false)
        {
            Type = type;
            Name = type;
            ChannelId = type + "-1";
            IsConnected = true;
            FailSends = failSends;
        }

        public override Task<bool> ConnectAsync() => Task.FromResult(true);

        public override Task DisconnectAsync() => Task.CompletedTask;

        public override Task<ChannelMessage> SendMessageAsync(string content)
        {
            if (FailSends) throw new InvalidOperationException("channel unavailable");

            Sent.Add(content);
            return Task.FromResult(new ChannelMessage { ChannelId = ChannelId, Content = content });
        }

        public override Task<List<ChannelMessage>> ReceiveMessagesAsync() =>
            Task.FromResult(new List<ChannelMessage>());
    }
}
