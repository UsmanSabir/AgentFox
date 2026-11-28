using System.Net;
using AgentFox.ChannelTests.Simulation;
using Microsoft.Extensions.Logging.Abstractions;
using TradingAgent.Config;
using TradingAgent.Market;
using TradingAgent.Observability;

namespace AgentFox.ChannelTests;

/// <summary>The dead-man (plan step 7): the health verdict, the heartbeat, and the stop worker's own report.</summary>
[TestClass]
public sealed class TradingHealthTests
{
    private static readonly DateTime Now = new(2026, 11, 30, 6, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Opened = Now.AddHours(-1);
    private static readonly DateTime Started = Now.AddHours(-2);

    private static (string Status, IReadOnlyList<string> Problems) Assess(
        bool session = true, bool read = true, bool recHealthy = true, DateTime? recAt = null, DateTime? priced = null,
        int openStops = 1, DateTime? pass = null, int unprotected = 0, MarketPhase phase = MarketPhase.Open,
        TradingStopMode stop = TradingStopMode.Off, DateTime? started = null) =>
        TradingHealth.Assess(Now, session, Opened, started ?? Started, read, recHealthy, recAt ?? Now.AddSeconds(-30),
            priced ?? Now.AddSeconds(-20), TimeSpan.FromSeconds(120), openStops, pass ?? Now.AddSeconds(-40),
            unprotected, phase, stop);

    [TestMethod]
    public void AHealthyAgentInASessionIsOk() => Assert.AreEqual("ok", Assess().Status);

    [TestMethod]
    public void OutsideASessionNothingIsJudged() =>
        Assert.AreEqual("resting", Assess(session: false, read: false, priced: Now.AddDays(-1)).Status);

    [TestMethod]
    public void NeverReadingTheAccountIsDown_AfterTheStartupGrace()
    {
        Assert.AreEqual("down", Assess(read: false).Status);
        Assert.AreEqual("ok", Assess(read: false, started: Now.AddMinutes(-2), priced: Now).Status, "still starting");
    }

    [TestMethod]
    public void OpenStopsWithAStaleStopPassIsDown() =>
        CollectionAssert.Contains(Assess(pass: Now.AddMinutes(-9)).Problems.ToList(), "stop-pass-stale");

    [TestMethod]
    public void EachDegradationIsNamed()
    {
        CollectionAssert.Contains(Assess(priced: Now.AddMinutes(-5)).Problems.ToList(), "feed-silent");
        CollectionAssert.Contains(Assess(recHealthy: false).Problems.ToList(), "reconciliation-stale");
        CollectionAssert.Contains(Assess(unprotected: 2).Problems.ToList(), "unprotected");
        CollectionAssert.Contains(Assess(phase: MarketPhase.Unknown).Problems.ToList(), "market-unknown");
        CollectionAssert.Contains(Assess(stop: TradingStopMode.FullStop).Problems.ToList(), "full-stop");
        Assert.AreEqual("degraded", Assess(unprotected: 1).Status);
    }

    [TestMethod]
    public async Task ARefusedStopIsReportedUnprotected_AndClearsOnceItIsPlaced()
    {
        using var h = new ProtectionHarness(0m, "PPL");
        await h.ProtectHoldingAsync("PPL", 100, 230m, trigger: 220m, limit: 217.80m);
        h.Broker.RejectNext(1, "Order Rejected! Chances of Wash Trade against this Order");

        await h.RunPassAsync();
        var refused = h.Worker.Health;
        Assert.AreEqual(1, refused.OpenStops);
        StringAssert.Contains(refused.Unprotected.Single().Why, "refused by the broker");

        h.Restart();
        await h.RunPassAsync();
        Assert.AreEqual(0, h.Worker.Health.Unprotected.Count);
        Assert.IsNotNull(h.Worker.Health.LastPassUtc);
    }

    [TestMethod]
    public async Task TheHeartbeatPostsTheReportWithTheTenantsToken()
    {
        var handler = new RecordingHandler();
        var options = new TradingAgentOptions
        {
            Watchdog = new WatchdogOptions { HeartbeatUrl = "http://watchdog.test/beat", Token = "t0ken", Tenant = "magma" }
        };
        var wrapped = Microsoft.Extensions.Options.Options.Create(options);
        var calendar = new SimCalendar();
        var reporter = new TradingHealthReporter(calendar, new TradingAgent.Reconciliation.TradingReconciliationState(),
            new TradingPolicyProvider(wrapped, new AgentFox.Plugins.PluginConfigManager(
                Path.Combine(Path.GetTempPath(), "hb-" + Guid.NewGuid().ToString("N")), NullLogger<AgentFox.Plugins.PluginConfigManager>.Instance)),
            wrapped);
        var sender = new TradingHeartbeatSender(reporter, calendar, wrapped, NullLogger<TradingHeartbeatSender>.Instance, handler);

        Assert.IsTrue(await sender.SendAsync("beat", default));

        var request = handler.Requests.Single();
        Assert.AreEqual("Bearer t0ken", request.Auth);
        StringAssert.Contains(request.Body, "\"kind\":\"beat\"");
        StringAssert.Contains(request.Body, "\"tenant\":\"magma\"");
        StringAssert.Contains(request.Body, "\"status\":");
    }

    [TestMethod]
    public async Task AHeartbeatTheWatchdogRefusesIsReportedNotThrown()
    {
        var handler = new RecordingHandler { Status = HttpStatusCode.Unauthorized };
        var options = Microsoft.Extensions.Options.Options.Create(new TradingAgentOptions
            { Watchdog = new WatchdogOptions { HeartbeatUrl = "http://watchdog.test/beat" } });
        var calendar = new SimCalendar();
        var reporter = new TradingHealthReporter(calendar, new TradingAgent.Reconciliation.TradingReconciliationState(),
            new TradingPolicyProvider(options, new AgentFox.Plugins.PluginConfigManager(
                Path.Combine(Path.GetTempPath(), "hb-" + Guid.NewGuid().ToString("N")), NullLogger<AgentFox.Plugins.PluginConfigManager>.Instance)),
            options);

        Assert.IsFalse(await new TradingHeartbeatSender(reporter, calendar, options,
            NullLogger<TradingHeartbeatSender>.Instance, handler).SendAsync("beat", default));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public List<(string? Auth, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Headers.Authorization?.ToString(), await request.Content!.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(Status);
        }
    }
}
