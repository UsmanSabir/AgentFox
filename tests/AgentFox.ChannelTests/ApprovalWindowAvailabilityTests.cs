using AgentFox.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using TradingAgent.Config;
using TradingAgent.Manager;
using TradingAgent.Market;

namespace AgentFox.ChannelTests;

/// <summary>
/// Pins <see cref="ApprovalGate.WindowCanTakeEffect"/>, which decides whether the Persistent orders
/// panel offers "Send without asking". Before it existed the button showed on every install and did
/// nothing on most: only ApprovalRequired execution with Approval.Mode = Window consults the window,
/// and Mode defaults to Always.
/// </summary>
[TestClass]
public sealed class ApprovalWindowAvailabilityTests
{
    [DataTestMethod]
    [DataRow("ApprovalRequired", "Window", true, true)]
    [DataRow("ApprovalRequired", "window", true, true)]
    [DataRow("ApprovalRequired", "Always", true, false)]
    [DataRow("ApprovalRequired", "Auto", true, false)]
    [DataRow("BoundedAuto", "Window", true, false)]
    [DataRow("Paper", "Window", true, false)]
    [DataRow("ApprovalRequired", "Window", false, false)]
    public void Window_takes_effect_only_under_ApprovalRequired_with_Window_mode(
        string executionMode, string approvalMode, bool autoExecute, bool expected)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"agentfox-approval-window-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            var configured = new TradingAgentOptions
            {
                AutoExecute = autoExecute,
                ExecutionMode = executionMode,
                DatabasePath = Path.Combine(temp, "trading.db")
            };
            configured.Approval.Mode = approvalMode;
            var options = Microsoft.Extensions.Options.Options.Create(configured);
            var policy = new TradingPolicyProvider(options,
                new PluginConfigManager(Path.Combine(temp, "plugin-config"), NullLogger<PluginConfigManager>.Instance));
            var calendar = new OpenCalendar();
            var gate = new ApprovalGate(new ApprovalIntentRegistry(), policy, calendar,
                TradingTestFactory.CalendarOnlyWindow(calendar), options, NullLogger<ApprovalGate>.Instance);

            Assert.AreEqual(expected, gate.WindowCanTakeEffect());
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* temp dir */ }
        }
    }

    private sealed class OpenCalendar : IMarketCalendar
    {
        public MarketStatus GetStatus(DateTime? utcNow = null) => new(true, DateTime.UtcNow.AddHours(5), "open");
    }
}
