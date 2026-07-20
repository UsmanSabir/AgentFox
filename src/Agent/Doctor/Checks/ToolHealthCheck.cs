namespace AgentFox.Doctor.Checks;

using AgentFox.Doctor;
using AgentFox.Tools;

public class ToolHealthCheck : IHealthCheckable
{
    private readonly ToolRegistry _toolRegistry;
    public string ComponentName => "Tools";

    public ToolHealthCheck(ToolRegistry toolRegistry) => _toolRegistry = toolRegistry;

    public async Task<IReadOnlyList<HealthCheckResult>> CheckHealthAsync(CancellationToken ct = default)
    {
        var results = new List<HealthCheckResult>();
        var allTools = _toolRegistry.GetAll();

        foreach (var tool in allTools)
        {
            if (tool is IHealthCheckable hc)
                results.AddRange(await hc.CheckHealthAsync(ct));
        }

        if (results.Count == 0)
            results.Add(new HealthCheckResult(
                HealthStatus.Healthy, "Tools",
                $"{allTools.Count} tool(s) registered — none implement IHealthCheckable"));

        return results;
    }
}
