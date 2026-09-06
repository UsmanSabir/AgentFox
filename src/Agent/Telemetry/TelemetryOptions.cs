namespace AgentFox.Telemetry;

/// <summary>
/// Configuration for OpenTelemetry export. Disabled by default: with <see cref="Enabled"/>
/// false no OpenTelemetry SDK is registered and AgentFox behaviour is unchanged.
///
/// This exists because a flag that CREATES spans is not the same thing as a pipeline that
/// COLLECTS them. <c>Harness:Profiles:*:EnableOpenTelemetry</c> only decides whether the
/// HarnessAgent writes to an <see cref="System.Diagnostics.ActivitySource"/>; until this
/// section registers a listener for that source name, every span it writes is dropped with
/// no error and no log line. <see cref="TelemetryRegistration"/> reads the Harness profiles
/// and warns when exactly that combination is configured.
/// </summary>
public sealed class TelemetryOptions
{
    public const string SectionName = "Telemetry";

    /// <summary>
    /// Source (and meter) name used by the main agent's chat-client instrumentation. Distinct
    /// from the Harness source so a trace shows which execution path produced it.
    /// </summary>
    public const string AgentSourceName = "AgentFox.Agent";

    /// <summary>Master switch. False (default) registers no SDK, no exporter, no listener.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Value reported as <c>service.name</c> on every emitted resource.</summary>
    public string ServiceName { get; set; } = "AgentFox";

    /// <summary>
    /// OTLP collector endpoint. When unset, <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is used — the
    /// convention the exporter itself honours, so either spelling works.
    /// </summary>
    public string? OtlpEndpoint { get; set; }

    /// <summary>Also write spans and metrics to the console. Intended for local development.</summary>
    public bool ConsoleExporter { get; set; } = false;

    /// <summary>
    /// Include prompts, responses and tool arguments in span attributes.
    /// FALSE BY DEFAULT AND DELIBERATELY SO: turning this on ships raw model input and output
    /// to the collector, which defeats the credential guard (see the Security section of
    /// appsettings.json) for anything a tool has read into context. Enable it only against a
    /// collector you would be willing to paste a conversation into.
    /// </summary>
    public bool CaptureMessageContent { get; set; } = false;

    /// <summary>
    /// Extra ActivitySource/Meter names to listen to, beyond <see cref="AgentSourceName"/> and
    /// the names declared by the configured Harness profiles. Plugins that instrument
    /// themselves belong here.
    /// </summary>
    public List<string> AdditionalSources { get; set; } = new();
}
