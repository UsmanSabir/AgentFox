using AgentFox.Harness;
using AgentFox.Plugins.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AgentFox.Telemetry;

/// <summary>
/// The single place AgentFox registers the OpenTelemetry SDK. Everything else emits through
/// <see cref="System.Diagnostics.ActivitySource"/>/<see cref="System.Diagnostics.Metrics.Meter"/>
/// and never references an exporter, so swapping backends stays inside this file.
/// </summary>
public static class TelemetryRegistration
{
    /// <summary>
    /// Binds <see cref="TelemetryOptions"/> and, when enabled, registers the tracer and meter
    /// providers for every source AgentFox can emit to. Behaviour-neutral when disabled: the
    /// options object is still registered (the agent builder reads it to decide whether to
    /// install chat-client instrumentation) but no SDK, listener or exporter is created.
    /// </summary>
    public static IServiceCollection AddAgentFoxTelemetry(
        this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(TelemetryOptions.SectionName).Get<TelemetryOptions>()
                      ?? new TelemetryOptions();
        services.Configure<TelemetryOptions>(configuration.GetSection(TelemetryOptions.SectionName));
        services.AddSingleton(options);

        // Always registered, including when telemetry is off — its job is to report the
        // enabled-but-silent combinations, which are precisely the ones nothing else notices.
        services.AddSingleton(sp => new TelemetryStartupReport(
            options,
            ResolveSourceNames(configuration, options),
            ResolveOtlpEndpoint(options),
            DescribeDroppedHarnessSources(configuration, options),
            sp.GetService<ILogger<TelemetryStartupReport>>()));
        services.AddHostedService(sp => sp.GetRequiredService<TelemetryStartupReport>());

        if (!options.Enabled)
            return services;

        var sources = ResolveSourceNames(configuration, options);
        var otlpEndpoint = ResolveOtlpEndpoint(options);

        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(
                serviceName: options.ServiceName,
                serviceVersion: typeof(TelemetryRegistration).Assembly.GetName().Version?.ToString()))
            .WithTracing(tracing =>
            {
                foreach (var source in sources)
                    tracing.AddSource(source);
                if (options.ConsoleExporter)
                    tracing.AddConsoleExporter();
                if (otlpEndpoint is not null)
                    tracing.AddOtlpExporter(o => ConfigureOtlp(o, otlpEndpoint, options, "/v1/traces"));
            })
            .WithMetrics(metrics =>
            {
                // Microsoft.Extensions.AI and the Harness name their Meter after the same string
                // they name their ActivitySource, so token-usage and duration histograms follow
                // the source list rather than needing a second one.
                foreach (var source in sources)
                    metrics.AddMeter(source);
                if (options.ConsoleExporter)
                    metrics.AddConsoleExporter();
                if (otlpEndpoint is not null)
                    metrics.AddOtlpExporter(o => ConfigureOtlp(o, otlpEndpoint, options, "/v1/metrics"));
            });

        return services;
    }

    /// <summary>
    /// Points one OTLP exporter at the collector, with the protocol and the signal path agreeing.
    ///
    /// <para>
    /// The signal path is the part that bites. Over <c>http/protobuf</c> the collector listens on
    /// <c>/v1/traces</c> and <c>/v1/metrics</c>, and the SDK only appends those itself when the
    /// endpoint came from the environment — an endpoint set in code suppresses the append, so a
    /// configured <c>http://host:4318</c> would POST to the root and get a 404 that never surfaces
    /// as an application error. Appending explicitly here makes both configuration routes behave
    /// the same. Over gRPC there is no path at all, and setting one breaks the export.
    /// </para>
    ///
    /// <para>
    /// The SDK's own <c>AppendSignalPathToEndpoint</c> switch is internal at 1.15.3, so this
    /// cannot be asserted from code — it was VERIFIED against a real collector instead, over both
    /// protocols. See <c>docker/otel/README.md</c> for the setup that verified it.
    /// </para>
    /// </summary>
    private static void ConfigureOtlp(
        OtlpExporterOptions exporter, Uri endpoint, TelemetryOptions options, string signalPath)
    {
        var protocol = ResolveProtocol(options);
        exporter.Protocol = protocol;

        if (protocol == OtlpExportProtocol.HttpProtobuf
            && (endpoint.AbsolutePath == "/" || endpoint.AbsolutePath.Length == 0))
        {
            exporter.Endpoint = new Uri(endpoint, signalPath);
        }
        else
        {
            exporter.Endpoint = endpoint;
        }
    }

    /// <summary>
    /// Config first, then <c>OTEL_EXPORTER_OTLP_PROTOCOL</c>, then gRPC — matching the OTLP
    /// specification's own default. An unrecognised value falls back to gRPC rather than throwing:
    /// a typo in a protocol name must not stop the host from starting.
    /// </summary>
    public static OtlpExportProtocol ResolveProtocol(TelemetryOptions options)
    {
        var raw = string.IsNullOrWhiteSpace(options.OtlpProtocol)
            ? Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL")
            : options.OtlpProtocol;

        return raw?.Trim().ToLowerInvariant() switch
        {
            "http/protobuf" or "httpprotobuf" or "http" => OtlpExportProtocol.HttpProtobuf,
            _ => OtlpExportProtocol.Grpc
        };
    }

    /// <summary>
    /// Every source name AgentFox can emit to: the agent's own, one per configured Harness
    /// profile, and anything named explicitly. Reading the profiles rather than hard-coding
    /// "AgentFox.Harness" is load-bearing — <see cref="HarnessProfileOptions.OpenTelemetrySourceName"/>
    /// is per-profile configuration, so a renamed source would otherwise go unlistened-to and
    /// reproduce the exact silent-drop this section exists to end.
    /// </summary>
    public static IReadOnlyList<string> ResolveSourceNames(
        IConfiguration configuration, TelemetryOptions options)
    {
        // Seeded from the shared contract list so a plugin that instruments itself against a
        // declared source is collected without the host being edited to know about it.
        var names = new SortedSet<string>(AgentTelemetry.KnownSourceNames, StringComparer.Ordinal);

        foreach (var profile in ReadHarnessProfiles(configuration).Values)
        {
            if (!string.IsNullOrWhiteSpace(profile.OpenTelemetrySourceName))
                names.Add(profile.OpenTelemetrySourceName);
        }

        foreach (var extra in options.AdditionalSources)
        {
            if (!string.IsNullOrWhiteSpace(extra))
                names.Add(extra);
        }

        return names.ToList();
    }

    /// <summary>
    /// Harness profiles that ask for telemetry while <see cref="TelemetryOptions.Enabled"/> is
    /// false — spans created and dropped. Empty when telemetry is on, or when no profile asks.
    /// </summary>
    public static IReadOnlyList<string> DescribeDroppedHarnessSources(
        IConfiguration configuration, TelemetryOptions options)
    {
        if (options.Enabled)
            return Array.Empty<string>();

        return ReadHarnessProfiles(configuration)
            .Where(p => p.Value.EnableOpenTelemetry)
            .Select(p => $"{p.Key} -> {p.Value.OpenTelemetrySourceName}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Config first, then the OTLP standard environment variable. Returns null when neither is
    /// set, which is a real state and not an error: console-only export is a valid local setup.
    /// </summary>
    public static Uri? ResolveOtlpEndpoint(TelemetryOptions options)
    {
        var raw = string.IsNullOrWhiteSpace(options.OtlpEndpoint)
            ? Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")
            : options.OtlpEndpoint;

        return Uri.TryCreate(raw, UriKind.Absolute, out var uri) ? uri : null;
    }

    /// <summary>
    /// Binds the Harness section the same way Program.cs does, so the profile defaults seeded in
    /// <see cref="HarnessOptions"/> are the ones read here too. An absent section yields those
    /// seeded defaults rather than an empty set.
    /// </summary>
    private static Dictionary<string, HarnessProfileOptions> ReadHarnessProfiles(IConfiguration configuration)
    {
        var harness = configuration.GetSection(HarnessOptions.SectionName).Get<HarnessOptions>()
                      ?? new HarnessOptions();
        return harness.Profiles;
    }
}
