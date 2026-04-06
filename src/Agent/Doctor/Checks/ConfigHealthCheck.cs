namespace AgentFox.Doctor.Checks;

using AgentFox.Doctor;
using Microsoft.Extensions.Configuration;

public class ConfigHealthCheck : IHealthCheckable
{
    private readonly IConfiguration _config;
    private readonly DoctorAgent? _doctorAgent;
    public string ComponentName => "Configuration";

    public ConfigHealthCheck(IConfiguration config, DoctorAgent? doctorAgent = null)
    {
        _config = config;
        _doctorAgent = doctorAgent;
    }

    public Task<IReadOnlyList<HealthCheckResult>> CheckHealthAsync(CancellationToken ct = default)
    {
        var results = new List<HealthCheckResult>();

        // Required LLM fields
        CheckRequiredKey("LLM:Provider", results);
        CheckRequiredKey("LLM:Model", results);

        // Embedding ModelRef must resolve to a Models section entry
        var modelRef = _config["Memory:ModelRef"];
        if (!string.IsNullOrWhiteSpace(modelRef))
        {
            var resolved = _config.GetSection($"Models:{modelRef}").Exists();
            results.Add(resolved
                ? Healthy($"Embedding ModelRef '{modelRef}' resolves to Models:{modelRef}")
                : Critical($"Embedding ModelRef '{modelRef}' not found under Models section", canFix: _doctorAgent != null, _doctorAgent != null ? "Ask DoctorAgent to fix" : null));
        }

        // Workspace path
        var workspacePath = _config["Workspace:Path"] ?? Directory.GetCurrentDirectory();
        results.Add(Directory.Exists(workspacePath)
            ? Healthy($"Workspace path exists: {workspacePath}")
            : Critical($"Workspace path missing: {workspacePath}", canFix: true, "Create missing directory"));

        return Task.FromResult<IReadOnlyList<HealthCheckResult>>(results);
    }

    public async Task<FixResult> TryFixAsync(HealthCheckResult result, CancellationToken ct = default)
    {
        // Directory creation (existing fix)
        if (result.Message.Contains("Workspace path missing") || result.FixDescription == "Create missing directory")
        {
            try
            {
                var workspacePath = _config["Workspace:Path"] ?? Directory.GetCurrentDirectory();
                Directory.CreateDirectory(workspacePath);
                return new FixResult(true, $"Created directory: {workspacePath}");
            }
            catch (Exception ex)
            {
                return new FixResult(false, $"Could not create directory: {ex.Message}");
            }
        }

        // LLM-assisted fix for config issues
        if (_doctorAgent != null)
            return await _doctorAgent.FixConfigIssueAsync(result.Message, ct);

        return new FixResult(false, "No DoctorAgent configured — cannot auto-fix this issue");
    }

    private void CheckRequiredKey(string key, List<HealthCheckResult> list)
    {
        var val = _config[key];
        list.Add(string.IsNullOrWhiteSpace(val)
            ? Critical($"Missing required config key: {key}", canFix: _doctorAgent != null, _doctorAgent != null ? "Ask DoctorAgent to fix" : null)
            : Healthy($"{key} = {val}"));
    }

    private static HealthCheckResult Healthy(string msg) =>
        new(HealthStatus.Healthy, "Configuration", msg);

    private static HealthCheckResult Critical(string msg, bool canFix, string? fixDesc = null) =>
        new(HealthStatus.Critical, "Configuration", msg, canFix, fixDesc);
}
