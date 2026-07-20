namespace AgentFox.Doctor;

public enum HealthStatus { Healthy, Warning, Critical }

public record HealthCheckResult(
    HealthStatus Status,
    string Component,
    string Message,
    bool CanAutoFix = false,
    string? FixDescription = null
);

public record FixResult(
    bool Success,
    string Message,
    bool RequiresRestart = false
);

/// <summary>
/// Opt-in contract for any component that can report its own health
/// and optionally attempt self-repair.
/// </summary>
public interface IHealthCheckable
{
    /// <summary>Human-readable category shown in the wizard header (e.g. "LLM Provider")</summary>
    string ComponentName { get; }

    Task<IReadOnlyList<HealthCheckResult>> CheckHealthAsync(CancellationToken ct = default);

    /// <summary>Called only when CanAutoFix=true and user/flag approved.</summary>
    Task<FixResult> TryFixAsync(HealthCheckResult result, CancellationToken ct = default)
        => Task.FromResult(new FixResult(false, "No fix available"));
}
