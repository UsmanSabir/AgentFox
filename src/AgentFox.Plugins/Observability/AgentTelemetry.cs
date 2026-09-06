using System.Diagnostics;

namespace AgentFox.Plugins.Observability;

/// <summary>
/// The ActivitySources AgentFox and its plugins emit to, and the helper that starts a span already
/// stamped with the ambient correlation id.
///
/// <para>
/// <b>These names are a contract, not a convention.</b> A span is collected only if something
/// registered a listener for its source name; the host's telemetry registration seeds its listener
/// list from <see cref="KnownSourceNames"/>, so a source declared here is collected and a source
/// invented at a call site is not. Adding a new one means adding it here.
/// </para>
///
/// <para>
/// <b>Only System.Diagnostics is used, deliberately — never OpenTelemetry.Api.</b>
/// <see cref="ActivitySource"/> lives in <c>System.Diagnostics.DiagnosticSource</c>, which ships in
/// the shared framework and therefore resolves to ONE type across the host and every plugin load
/// context. Taking a <c>PackageReference</c> on OpenTelemetry.Api (or on DiagnosticSource itself)
/// from a plugin would copy that assembly beside the plugin, where <c>AssemblyDependencyResolver</c>
/// would load a second copy into the plugin's context — a distinct <c>ActivitySource</c> type that
/// the host's listener cannot see. The spans would still be created, and would silently never be
/// collected. <c>PluginLoadContext</c> now delegates that assembly by name as a backstop.
/// </para>
/// </summary>
public static class AgentTelemetry
{
    /// <summary>Agent turns, tool calls and approval decisions — the host's own execution.</summary>
    public const string AgentSourceName = "AgentFox.Agent";

    /// <summary>Broker submissions and reconciliation runs — the trading plugin.</summary>
    public const string TradingSourceName = "AgentFox.Trading";

    /// <summary>Every source the host listens to by default. See the class remarks.</summary>
    public static readonly IReadOnlyList<string> KnownSourceNames =
        [AgentSourceName, TradingSourceName];

    /// <summary>Attribute carrying <see cref="CorrelationContext.Current"/> on every span.</summary>
    public const string CorrelationIdAttribute = "agentfox.correlation_id";

    public static readonly ActivitySource Agent = new(AgentSourceName);
    public static readonly ActivitySource Trading = new(TradingSourceName);

    /// <summary>
    /// Starts a span and stamps the ambient correlation id on it.
    ///
    /// <para>
    /// Returns null when nothing is listening — that is <see cref="ActivitySource.StartActivity"/>'s
    /// own contract, not an error, and it is what keeps instrumentation free when telemetry is off.
    /// Every call site must therefore use <c>?.</c> and must not depend on the span existing;
    /// an instrumented path whose BEHAVIOUR changes with telemetry enabled is a defect.
    /// </para>
    /// </summary>
    public static Activity? Start(
        ActivitySource source, string name, ActivityKind kind = ActivityKind.Internal)
    {
        var activity = source.StartActivity(name, kind);
        if (activity is not null && CorrelationContext.Current is { } correlationId)
            activity.SetTag(CorrelationIdAttribute, correlationId);

        return activity;
    }

    /// <summary>
    /// Records the outcome on a span: <c>Ok</c>, or <c>Error</c> with the reason as the status
    /// description. Null-tolerant so a call site never branches on whether telemetry is on.
    ///
    /// <para>
    /// A REFUSAL is recorded as an error status with its reason, not as a successful span. That
    /// is the point of instrumenting a system whose safe behaviour is to decline: "why did nothing
    /// happen" is the question asked most, and a refusal that traces as success cannot answer it.
    /// </para>
    /// </summary>
    public static void SetOutcome(Activity? activity, bool succeeded, string? reason = null)
    {
        if (activity is null) return;

        activity.SetTag("agentfox.outcome", succeeded ? "ok" : "refused");
        if (!string.IsNullOrWhiteSpace(reason))
            activity.SetTag("agentfox.reason", reason);

        activity.SetStatus(succeeded ? ActivityStatusCode.Ok : ActivityStatusCode.Error, reason);
    }

    /// <summary>Records a thrown exception and marks the span failed.</summary>
    public static void SetError(Activity? activity, Exception exception)
    {
        if (activity is null) return;

        activity.SetTag("agentfox.outcome", "error");
        activity.SetTag("error.type", exception.GetType().FullName);
        activity.SetStatus(ActivityStatusCode.Error, exception.Message);
    }
}
