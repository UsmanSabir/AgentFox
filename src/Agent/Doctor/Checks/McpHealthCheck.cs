namespace AgentFox.Doctor.Checks;

using AgentFox.Doctor;
using AgentFox.MCP;
using Microsoft.Extensions.Configuration;

public class McpHealthCheck : IHealthCheckable
{
    private readonly MCPClient _mcpClient;
    private readonly IConfiguration _config;
    private readonly DoctorAgent? _doctorAgent;

    public string ComponentName => "MCP";

    public McpHealthCheck(MCPClient mcpClient, IConfiguration config, DoctorAgent? doctorAgent = null)
    {
        _mcpClient = mcpClient;
        _config = config;
        _doctorAgent = doctorAgent;
    }

    public Task<IReadOnlyList<HealthCheckResult>> CheckHealthAsync(CancellationToken ct = default)
    {
        var results = new List<HealthCheckResult>();

        // If MCP is disabled (or the key is absent), report healthy and skip further checks.
        var mcpEnabledRaw = _config["MCP:Enabled"];
        bool mcpEnabled = !string.IsNullOrWhiteSpace(mcpEnabledRaw)
                          && bool.TryParse(mcpEnabledRaw, out var parsed)
                          && parsed;

        if (!mcpEnabled)
        {
            results.Add(Healthy("MCP disabled"));
            return Task.FromResult<IReadOnlyList<HealthCheckResult>>(results);
        }

        // MCP is enabled — inspect server connections.
        var allServers      = _mcpClient.Servers;
        var connectedServers = _mcpClient.GetConnectedServers();

        if (allServers.Count == 0)
        {
            results.Add(Warning("MCP enabled but no servers are configured"));
            return Task.FromResult<IReadOnlyList<HealthCheckResult>>(results);
        }

        // Report overall connection status.
        if (connectedServers.Count == allServers.Count)
        {
            results.Add(Healthy($"All {allServers.Count} MCP server(s) connected"));
        }
        else if (connectedServers.Count > 0)
        {
            results.Add(Warning(
                $"{connectedServers.Count}/{allServers.Count} MCP server(s) connected"));
        }
        else
        {
            results.Add(Critical(
                $"MCP enabled but none of the {allServers.Count} configured server(s) are connected"));
        }

        // Report tool count per connected server.
        int totalTools = 0;
        foreach (var server in connectedServers)
        {
            var toolCount = server.AvailableTools.Count;
            totalTools += toolCount;
            results.Add(Healthy($"Server '{server.Name}': {toolCount} tool(s) registered"));
        }

        // Also report servers that failed to connect.
        foreach (var kvp in allServers)
        {
            if (!kvp.Value.IsConnected)
            {
                results.Add(new HealthCheckResult(
                    HealthStatus.Critical, "MCP",
                    $"Server '{kvp.Key}' is not connected — check URL and credentials in appsettings.json MCP:Servers",
                    CanAutoFix: _doctorAgent != null,
                    FixDescription: _doctorAgent != null ? "Ask DoctorAgent to update MCP server config" : null));
            }
        }

        if (connectedServers.Count > 0)
        {
            results.Add(Healthy($"Total MCP tools registered: {totalTools}"));
        }

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
        => new(HealthStatus.Healthy,  "MCP", msg);

    private static HealthCheckResult Warning(string msg)
        => new(HealthStatus.Warning,  "MCP", msg);

    private static HealthCheckResult Critical(string msg, bool canFix = false, string? fixDesc = null)
        => new(HealthStatus.Critical, "MCP", msg, canFix, fixDesc);
}
