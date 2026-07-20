namespace AgentFox.Doctor;

/// <summary>
/// Iterates all registered IHealthCheckable components, reports results via DoctorUI,
/// and applies fixes when approved. Has no direct Spectre.Console dependency.
/// </summary>
public class DoctorRunner
{
    private readonly IReadOnlyList<IHealthCheckable> _checks;

    public DoctorRunner(IEnumerable<IHealthCheckable> checks)
        => _checks = checks.ToList();

    /// <param name="autoFix">True when --fix flag was passed. Non-destructive fixes run automatically;
    /// destructive fixes still prompt via DoctorUI.ConfirmDestructive inside TryFixAsync.</param>
    public async Task RunAsync(bool autoFix, CancellationToken ct = default)
    {
        DoctorUI.PrintBanner();
        int healthy = 0, warnings = 0, critical = 0;

        foreach (var check in _checks)
        {
            DoctorUI.PrintComponentHeader(check.ComponentName);
            IReadOnlyList<HealthCheckResult> results;

            try
            {
                results = await check.CheckHealthAsync(ct);
            }
            catch (Exception ex)
            {
                DoctorUI.ReportCritical($"Check threw exception: {ex.Message}");
                critical++;
                continue;
            }

            foreach (var result in results)
            {
                DoctorUI.ReportResult(result);

                switch (result.Status)
                {
                    case HealthStatus.Healthy:  healthy++;  break;
                    case HealthStatus.Warning:  warnings++; break;
                    case HealthStatus.Critical: critical++; break;
                }

                if (!result.CanAutoFix) continue;

                bool shouldFix;
                if (autoFix)
                {
                    // --fix flag: proceed automatically for non-destructive;
                    // destructive ops are gated inside TryFixAsync via ConfirmDestructive
                    shouldFix = true;
                }
                else
                {
                    // Interactive mode: offer fix only for Critical issues
                    shouldFix = result.Status == HealthStatus.Critical &&
                        DoctorUI.Confirm($"Attempt fix: {result.FixDescription}?", defaultValue: false);
                }

                if (shouldFix)
                {
                    var fix = await check.TryFixAsync(result, ct);
                    if (fix.Success)
                        DoctorUI.ReportFixApplied(fix.Message);
                    else
                        DoctorUI.ReportFixFailed(fix.Message);
                }
            }
        }

        DoctorUI.PrintSummary(healthy, warnings, critical);
    }
}
