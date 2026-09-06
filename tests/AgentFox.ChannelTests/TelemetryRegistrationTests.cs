using System.Diagnostics;
using AgentFox.Harness;
using AgentFox.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentFox.ChannelTests;

/// <summary>
/// Pins the property this telemetry wiring exists to guarantee: a source that something WRITES
/// to is a source something LISTENS to, and any configuration where that is not true announces
/// itself at startup instead of silently producing nothing.
///
/// The bug being prevented shipped for months — two Harness profiles defaulted
/// EnableOpenTelemetry to true while the solution referenced no OpenTelemetry SDK at all, so
/// every span they wrote was dropped with no error and no log line.
/// </summary>
[TestClass]
public sealed class TelemetryRegistrationTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [TestMethod]
    public void TelemetryIsDisabledByDefault_AndCapturesNoMessageContent()
    {
        var options = new TelemetryOptions();

        Assert.IsFalse(options.Enabled, "Telemetry must be opt-in.");
        Assert.IsFalse(options.CaptureMessageContent,
            "Prompts and responses must never leave the process without an explicit opt-in.");
        Assert.IsFalse(options.ConsoleExporter);
    }

    [TestMethod]
    public void SourceNames_CoverTheAgentAndEveryConfiguredHarnessProfile()
    {
        var sources = TelemetryRegistration.ResolveSourceNames(
            Config(new Dictionary<string, string?>()), new TelemetryOptions());

        CollectionAssert.Contains(sources.ToList(), TelemetryOptions.AgentSourceName);

        // Every profile's declared source must be listened to, or that profile's spans vanish.
        foreach (var profile in new HarnessOptions().Profiles.Values)
            CollectionAssert.Contains(sources.ToList(), profile.OpenTelemetrySourceName);
    }

    [TestMethod]
    public void SourceNames_FollowARenamedHarnessProfileSource()
    {
        // The whole reason the source list is DERIVED from the Harness profiles rather than
        // hard-coded: OpenTelemetrySourceName is per-profile configuration, and a hard-coded
        // listener would silently stop collecting the moment someone renamed it.
        var configuration = Config(new Dictionary<string, string?>
        {
            ["Harness:Profiles:trading-research:OpenTelemetrySourceName"] = "Custom.Renamed.Source",
        });

        var sources = TelemetryRegistration.ResolveSourceNames(configuration, new TelemetryOptions());

        CollectionAssert.Contains(sources.ToList(), "Custom.Renamed.Source");
    }

    [TestMethod]
    public void SourceNames_IncludeAdditionalSources()
    {
        var options = new TelemetryOptions { AdditionalSources = { "Plugin.TradingAgent" } };

        var sources = TelemetryRegistration.ResolveSourceNames(
            Config(new Dictionary<string, string?>()), options);

        CollectionAssert.Contains(sources.ToList(), "Plugin.TradingAgent");
    }

    [TestMethod]
    public void HarnessProfileAskingForTelemetryWhileCollectionIsOff_IsReportedAsAWarning()
    {
        // This is the exact shipped-and-silent state, reproduced.
        var configuration = Config(new Dictionary<string, string?>());
        var options = new TelemetryOptions { Enabled = false };

        var dropped = TelemetryRegistration.DescribeDroppedHarnessSources(configuration, options);
        Assert.IsTrue(dropped.Count > 0,
            "The seeded profiles enable OpenTelemetry, so with collection off they must be reported.");

        var report = new TelemetryStartupReport(options, [], null, dropped, null).BuildReport();

        Assert.AreEqual(1, report.Count);
        Assert.AreEqual(LogLevel.Warning, report[0].Level);
        StringAssert.Contains(report[0].Message, "created and dropped");
    }

    [TestMethod]
    public void NoHarnessProfileAskingForTelemetry_ReportsNothingWhenCollectionIsOff()
    {
        var configuration = Config(new Dictionary<string, string?>
        {
            ["Harness:Profiles:trading-research:EnableOpenTelemetry"] = "false",
            ["Harness:Profiles:developer-sandbox:EnableOpenTelemetry"] = "false",
        });
        var options = new TelemetryOptions { Enabled = false };

        var dropped = TelemetryRegistration.DescribeDroppedHarnessSources(configuration, options);
        Assert.AreEqual(0, dropped.Count);

        var report = new TelemetryStartupReport(options, [], null, dropped, null).BuildReport();
        Assert.AreEqual(0, report.Count, "Telemetry off with nothing emitting is silent, not noisy.");
    }

    [TestMethod]
    public void EnabledWithNoExporter_IsReportedAsAWarning()
    {
        // Collecting into no exporter is the same class of failure as writing to no listener.
        var options = new TelemetryOptions { Enabled = true, ConsoleExporter = false };

        var report = new TelemetryStartupReport(
            options, [TelemetryOptions.AgentSourceName], null, [], null).BuildReport();

        Assert.AreEqual(LogLevel.Warning, report[0].Level);
        StringAssert.Contains(report[0].Message, "no exporter is configured");
    }

    [TestMethod]
    public void EnabledWithAnExporter_ReportsTheSourcesItIsCollecting()
    {
        var options = new TelemetryOptions { Enabled = true, ConsoleExporter = true };

        var report = new TelemetryStartupReport(
            options, [TelemetryOptions.AgentSourceName, "AgentFox.Harness"], null, [], null).BuildReport();

        Assert.AreEqual(1, report.Count);
        Assert.AreEqual(LogLevel.Information, report[0].Level);
        StringAssert.Contains(report[0].Message, "AgentFox.Harness");
        StringAssert.Contains(report[0].Message, "console");
    }

    [TestMethod]
    public void CaptureMessageContent_WarnsThatContentLeavesTheProcess()
    {
        var options = new TelemetryOptions
        {
            Enabled = true,
            ConsoleExporter = true,
            CaptureMessageContent = true,
        };

        var report = new TelemetryStartupReport(options, ["x"], null, [], null).BuildReport();

        Assert.IsTrue(report.Any(l => l.Level == LogLevel.Warning && l.Message.Contains("leaves the process")),
            "Exporting raw prompts and responses must not happen quietly.");
    }

    [TestMethod]
    public async Task WhenEnabled_SomethingActuallyListensToEverySourceAgentFoxEmitsTo()
    {
        // The end-to-end assertion, and the one that would have caught the original gap: not
        // "is the flag set" but "does an ActivitySource with this name have a listener". Before
        // the SDK was referenced, every one of these was false while the config looked correct.
        var configuration = Config(new Dictionary<string, string?>
        {
            ["Telemetry:Enabled"] = "true",
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentFoxTelemetry(configuration);

        await using var provider = services.BuildServiceProvider();
        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StartAsync(CancellationToken.None);

        var expected = TelemetryRegistration.ResolveSourceNames(configuration, new TelemetryOptions());
        Assert.IsTrue(expected.Count > 1, "Sanity: the agent source plus the Harness profiles.");

        foreach (var name in expected)
        {
            using var source = new ActivitySource(name);
            Assert.IsTrue(source.HasListeners(), $"No listener is attached to '{name}' — its spans would be dropped.");
        }
    }

    [TestMethod]
    public async Task WhenDisabled_NoListenerIsAttachedAndNothingIsRegistered()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentFoxTelemetry(Config(new Dictionary<string, string?>()));

        await using var provider = services.BuildServiceProvider();
        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StartAsync(CancellationToken.None);

        using var source = new ActivitySource(TelemetryOptions.AgentSourceName);
        Assert.IsFalse(source.HasListeners(), "Disabled telemetry must register no SDK at all.");
    }

    [TestMethod]
    public void OtlpEndpoint_PrefersConfigurationThenFallsBackToTheStandardEnvironmentVariable()
    {
        const string envKey = "OTEL_EXPORTER_OTLP_ENDPOINT";
        var original = Environment.GetEnvironmentVariable(envKey);
        try
        {
            Environment.SetEnvironmentVariable(envKey, "http://collector.env:4317");

            Assert.AreEqual(
                new Uri("http://collector.config:4317"),
                TelemetryRegistration.ResolveOtlpEndpoint(
                    new TelemetryOptions { OtlpEndpoint = "http://collector.config:4317" }));

            Assert.AreEqual(
                new Uri("http://collector.env:4317"),
                TelemetryRegistration.ResolveOtlpEndpoint(new TelemetryOptions()));

            Environment.SetEnvironmentVariable(envKey, null);
            Assert.IsNull(TelemetryRegistration.ResolveOtlpEndpoint(new TelemetryOptions()),
                "No endpoint anywhere is a real state, not a parse error.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(envKey, original);
        }
    }
}
