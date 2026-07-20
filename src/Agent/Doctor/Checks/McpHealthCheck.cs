namespace AgentFox.Doctor.Checks;

using AgentFox.Doctor;
using AgentFox.MCP;
using Microsoft.Extensions.Configuration;

public class McpHealthCheck : IHealthCheckable
{
    private readonly McpManager _mcpManager;
    private readonly IConfiguration _config;
    private readonly DoctorAgent? _doctorAgent;

    public string ComponentName => "MCP";

    public McpHealthCheck(McpManager mcpManager, IConfiguration config, DoctorAgent? doctorAgent = null)
    {
        _mcpManager = mcpManager;
        _config = config;
        _doctorAgent = doctorAgent;
    }

    public Task<IReadOnlyList<HealthCheckResult>> CheckHealthAsync(CancellationToken ct = default)
    {
        var results = new List<HealthCheckResult>();

        var connected = _mcpManager.GetConnectedServers();
        var failures  = _mcpManager.Failures;
        var totalConfigured = connected.Count + failures.Count;

        if (totalConfigured == 0)
        {
            results.Add(Warning("MCP enabled but no servers are configured"));
            return Task.FromResult<IReadOnlyList<HealthCheckResult>>(results);
        }

        // Overall connection summary
        if (failures.Count == 0)
            results.Add(Healthy($"All {totalConfigured} MCP server(s) connected"));
        else if (connected.Count > 0)
            results.Add(Warning($"{connected.Count}/{totalConfigured} MCP server(s) connected"));
        else
            results.Add(Critical($"MCP enabled but none of the {totalConfigured} configured server(s) are connected"));

        // Per-server tool counts
        var totalTools = 0;
        foreach (var (name, toolCount, _) in connected)
        {
            totalTools += toolCount;
            results.Add(Healthy($"Server '{name}': {toolCount} tool(s) registered"));
        }

        // Failed servers with error detail
        foreach (var (name, error) in failures)
        {
            results.Add(new HealthCheckResult(
                HealthStatus.Critical, "MCP",
                $"Server '{name}' failed to connect: {error}",
                CanAutoFix: _doctorAgent != null,
                FixDescription: _doctorAgent != null ? "Ask DoctorAgent to update MCP server config" : null));
        }

        if (connected.Count > 0)
            results.Add(Healthy($"Total MCP tools registered: {totalTools}"));

        return Task.FromResult<IReadOnlyList<HealthCheckResult>>(results);
    }

    public async Task<FixResult> TryFixAsync(HealthCheckResult result, CancellationToken ct = default)
    {
        if (_doctorAgent != null)
            return await _doctorAgent.FixConfigIssueAsync(
                $"MCP server configuration issue: {result.Message}", ct);

        return new FixResult(false, "No DoctorAgent configured — cannot auto-fix MCP config");
    }

    private static HealthCheckResult Healthy(string msg)
        => new(HealthStatus.Healthy, "MCP", msg);

    private static HealthCheckResult Warning(string msg)
        => new(HealthStatus.Warning, "MCP", msg);

    private static HealthCheckResult Critical(string msg, bool canFix = false, string? fixDesc = null)
        => new(HealthStatus.Critical, "MCP", msg, canFix, fixDesc);
}
