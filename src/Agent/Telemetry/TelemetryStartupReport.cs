using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentFox.Telemetry;

/// <summary>
/// Says out loud, once at startup, what telemetry will and will not collect.
///
/// This exists because the failure mode being fixed here was entirely silent: two Harness
/// profiles shipped with <c>EnableOpenTelemetry: true</c> for months while no OpenTelemetry SDK
/// was referenced anywhere in the solution, so every span they wrote went to a source with no
/// listener. Nothing failed, nothing logged, and the flag read exactly like it was working.
/// A configuration that produces no signal must therefore announce itself rather than be
/// discovered later by someone wondering where their traces went.
/// </summary>
public sealed class TelemetryStartupReport : IHostedService
{
    private readonly TelemetryOptions _options;
    private readonly IReadOnlyList<string> _sources;
    private readonly Uri? _otlpEndpoint;
    private readonly IReadOnlyList<string> _droppedHarnessSources;
    private readonly ILogger<TelemetryStartupReport>? _logger;

    public TelemetryStartupReport(
        TelemetryOptions options,
        IReadOnlyList<string> sources,
        Uri? otlpEndpoint,
        IReadOnlyList<string> droppedHarnessSources,
        ILogger<TelemetryStartupReport>? logger)
    {
        _options = options;
        _sources = sources;
        _otlpEndpoint = otlpEndpoint;
        _droppedHarnessSources = droppedHarnessSources;
        _logger = logger;
    }

    /// <summary>
    /// The lines this reporter would write, in order. Exposed so tests assert on the decision
    /// rather than on a captured log sink.
    /// </summary>
    public IReadOnlyList<(LogLevel Level, string Message)> BuildReport()
    {
        var lines = new List<(LogLevel, string)>();

        if (!_options.Enabled)
        {
            if (_droppedHarnessSources.Count > 0)
            {
                lines.Add((LogLevel.Warning,
                    $"Telemetry:Enabled is false, but {_droppedHarnessSources.Count} Harness profile(s) " +
                    $"request OpenTelemetry ({string.Join("; ", _droppedHarnessSources)}). Those spans are " +
                    "created and dropped — set Telemetry:Enabled=true with an exporter to collect them."));
            }
            return lines;
        }

        var exporters = new List<string>();
        if (_otlpEndpoint is not null) exporters.Add($"OTLP -> {_otlpEndpoint}");
        if (_options.ConsoleExporter) exporters.Add("console");

        if (exporters.Count == 0)
        {
            lines.Add((LogLevel.Warning,
                "Telemetry:Enabled is true but no exporter is configured — spans and metrics are " +
                "collected and discarded. Set Telemetry:OtlpEndpoint (or OTEL_EXPORTER_OTLP_ENDPOINT), " +
                "or Telemetry:ConsoleExporter=true."));
        }
        else
        {
            lines.Add((LogLevel.Information,
                $"Telemetry enabled for '{_options.ServiceName}'. Sources: {string.Join(", ", _sources)}. " +
                $"Exporters: {string.Join(", ", exporters)}."));
        }

        if (_options.CaptureMessageContent)
        {
            lines.Add((LogLevel.Warning,
                "Telemetry:CaptureMessageContent is true — prompts, model responses and tool arguments " +
                "are exported as span attributes. Anything a tool has read into context leaves the process."));
        }

        return lines;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var (level, message) in BuildReport())
            _logger?.Log(level, "{TelemetryReport}", message);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
