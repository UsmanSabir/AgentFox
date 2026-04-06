namespace AgentFox.Doctor.Checks;

using AgentFox.Doctor;
using Microsoft.Extensions.Configuration;

public class ConfigHealthCheck : IHealthCheckable
{
    private readonly IConfiguration _config;
    public string ComponentName => "Configuration";

    public ConfigHealthCheck(IConfiguration config) => _config = config;

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
                : Critical($"Embedding ModelRef '{modelRef}' not found under Models section", canFix: false));
        }

        // Workspace path
        var workspacePath = _config["Workspace:Path"] ?? Directory.GetCurrentDirectory();
        results.Add(Directory.Exists(workspacePath)
            ? Healthy($"Workspace path exists: {workspacePath}")
            : Critical($"Workspace path missing: {workspacePath}", canFix: true, "Create missing directory"));

        return Task.FromResult<IReadOnlyList<HealthCheckResult>>(results);
    }

    public Task<FixResult> TryFixAsync(HealthCheckResult result, CancellationToken ct = default)
    {
        // Only fix is creating the missing workspace directory
        try
        {
            var workspacePath = _config["Workspace:Path"] ?? Directory.GetCurrentDirectory();
            Directory.CreateDirectory(workspacePath);
            return Task.FromResult(new FixResult(true, $"Created directory: {workspacePath}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new FixResult(false, $"Could not create directory: {ex.Message}"));
        }
    }

    private void CheckRequiredKey(string key, List<HealthCheckResult> list)
    {
        var val = _config[key];
        list.Add(string.IsNullOrWhiteSpace(val)
            ? Critical($"Missing required config key: {key}", canFix: false)
            : Healthy($"{key} = {val}"));
    }

    private static HealthCheckResult Healthy(string msg) =>
        new(HealthStatus.Healthy, "Configuration", msg);

    private static HealthCheckResult Critical(string msg, bool canFix, string? fixDesc = null) =>
        new(HealthStatus.Critical, "Configuration", msg, canFix, fixDesc);
}
