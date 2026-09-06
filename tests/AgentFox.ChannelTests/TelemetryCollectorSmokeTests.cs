using AgentFox.Plugins.Observability;
using AgentFox.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Exporter;

namespace AgentFox.ChannelTests;

/// <summary>
/// End-to-end export against a REAL OpenTelemetry Collector. Opt-in, following the same
/// convention as the AHK live tests: skipped unless <c>OTEL_SMOKE_ENDPOINT</c> is set, because CI
/// has no collector and a test that silently needs one is worse than one that says so.
///
/// <para>
/// Run it with:
/// <code>
/// docker compose -f docker/otel/docker-compose.yml up -d
/// OTEL_SMOKE_ENDPOINT=http://localhost:4317 dotnet test    # gRPC
/// OTEL_SMOKE_ENDPOINT=http://localhost:4318 OTEL_SMOKE_PROTOCOL=http/protobuf dotnet test
/// </code>
/// then confirm the span in <c>docker logs agentfox-otel-collector</c> or at
/// <c>http://localhost:16686</c>.
/// </para>
///
/// <para>
/// This exists because the thing most likely to be wrong about an exporter cannot be unit tested:
/// whether the protocol matches the port, and whether the signal path is right over HTTP. Both
/// fail by exporting into a void — the SDK retries in the background and the application never
/// notices — so the only proof is a collector that received something.
/// </para>
/// </summary>
[TestClass]
public sealed class TelemetryCollectorSmokeTests
{
    [TestMethod]
    public async Task SpansReachARunningCollector()
    {
        var endpoint = Environment.GetEnvironmentVariable("OTEL_SMOKE_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            Assert.Inconclusive(
                "Set OTEL_SMOKE_ENDPOINT (e.g. http://localhost:4317) with a collector running "
                + "to exercise a real export. See docker/otel/README.md.");
            return;
        }

        var protocol = Environment.GetEnvironmentVariable("OTEL_SMOKE_PROTOCOL");
        var marker = "smoke-" + Guid.NewGuid().ToString("N")[..12];

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Telemetry:Enabled"] = "true",
                ["Telemetry:ServiceName"] = "AgentFox-Smoke",
                ["Telemetry:OtlpEndpoint"] = endpoint,
                ["Telemetry:OtlpProtocol"] = protocol,
            }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentFoxTelemetry(configuration);

        await using (var provider = services.BuildServiceProvider())
        {
            foreach (var hosted in provider.GetServices<IHostedService>())
                await hosted.StartAsync(CancellationToken.None);

            using (CorrelationContext.Begin(marker))
            {
                using var span = AgentTelemetry.Start(AgentTelemetry.Agent, "smoke.export");
                Assert.IsNotNull(span, "Telemetry is enabled, so this source must be sampled.");
                span.SetTag("agentfox.smoke.marker", marker);
            }

            // Disposing the provider shuts the exporter down, which flushes. Without this the
            // process could exit with the batch still queued — the export "succeeding" while
            // nothing was ever sent.
        }

        Console.WriteLine(
            $"Exported span 'smoke.export' with marker {marker} to {endpoint} "
            + $"({TelemetryRegistration.ResolveProtocol(new TelemetryOptions { OtlpProtocol = protocol })}). "
            + "Confirm with: docker logs agentfox-otel-collector");
    }

    [TestMethod]
    public void ProtocolResolutionMatchesTheOtlpSpecDefault()
    {
        // Not a smoke test — runs everywhere. Picking the wrong protocol is silent, so the
        // mapping is worth pinning even without a collector.
        Assert.AreEqual(OtlpExportProtocol.Grpc,
            TelemetryRegistration.ResolveProtocol(new TelemetryOptions()));

        Assert.AreEqual(OtlpExportProtocol.HttpProtobuf,
            TelemetryRegistration.ResolveProtocol(new TelemetryOptions { OtlpProtocol = "http/protobuf" }));

        Assert.AreEqual(OtlpExportProtocol.Grpc,
            TelemetryRegistration.ResolveProtocol(new TelemetryOptions { OtlpProtocol = "grpc" }));

        // A typo must not stop the host from starting; it falls back to the spec default.
        Assert.AreEqual(OtlpExportProtocol.Grpc,
            TelemetryRegistration.ResolveProtocol(new TelemetryOptions { OtlpProtocol = "gRPC-ish" }));
    }
}
