using AgentFox.Harness;
using AgentFox.Plugins.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
                    tracing.AddOtlpExporter(o => o.Endpoint = otlpEndpoint);
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
                    metrics.AddOtlpExporter(o => o.Endpoint = otlpEndpoint);
            });

        return services;
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
