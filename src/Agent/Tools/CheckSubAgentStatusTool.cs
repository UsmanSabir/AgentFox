using AgentFox.Agents;
using AgentFox.Plugins.Interfaces;
using Microsoft.Extensions.Logging;

namespace AgentFox.Tools;

/// <summary>
/// Reports the status of background sub-agents — both those still running and those that have
/// recently finished. Results normally arrive on their own via a result announcement; this tool
/// is the fallback for when one does not, so that a spawned background sub-agent is never
/// write-only from the parent agent's point of view.
/// </summary>
public class CheckSubAgentStatusTool : BaseTool
{
    private readonly SubAgentManager _subAgentManager;
    private readonly ILogger? _logger;

    public CheckSubAgentStatusTool(SubAgentManager subAgentManager, ILogger? logger = null)
    {
        _subAgentManager = subAgentManager;
        _logger = logger;
    }

    public override string Name => "check_subagent_status";

    public override string Description =>
        "Check the status and results of background sub-agents spawned with spawn_background_subagent. " +
        "Lists sub-agents that are still running and those that recently finished, including their " +
        "output or error. Use this when a background sub-agent has not reported its result back, or " +
        "to confirm whether a background task is still working before waiting on it further.";

    public override Dictionary<string, Plugins.Interfaces.ToolParameter> Parameters { get; } = new()
    {
        ["run_id"] = new()
        {
            Type = "string",
            Description = "Optional run ID to report on a single sub-agent. Omit to list all of them.",
            Required = false
        },
        ["include_output"] = new()
        {
            Type = "boolean",
            Description = "Include the full output of finished sub-agents (default: true). " +
                          "Set false for a compact status-only listing.",
            Required = false
        }
    };

    protected override Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        try
        {
            var runId = arguments.GetValueOrDefault("run_id")?.ToString();
            var includeOutput = arguments.GetValueOrDefault("include_output") is not false;

            // Scope to the session that is asking, matching how spawn_background_subagent routes
            // results, so one conversation never sees another conversation's sub-agent output.
            var sessionKey = FoxAgent.CurrentSessionKey.Value;

            var active = _subAgentManager.GetActiveSubAgents()
                .Where(t => sessionKey == null || t.ParentSessionKey == sessionKey)
                .OrderBy(t => t.CreatedAt)
                .ToList();

            var finished = _subAgentManager.GetRecentCompletions()
                .Where(r => sessionKey == null || r.ParentSessionKey == sessionKey)
                .ToList();

            if (!string.IsNullOrWhiteSpace(runId))
                return Task.FromResult(DescribeSingle(runId, active, finished, includeOutput));

            if (active.Count == 0 && finished.Count == 0)
                return Task.FromResult(ToolResult.Ok(
                    "No background sub-agents are running, and none have finished recently."));

            var report = new System.Text.StringBuilder();

            report.AppendLine($"Running background sub-agents: {active.Count}");
            foreach (var task in active)
            {
                report.AppendLine();
                report.AppendLine($"  • {task.RunId} — {task.State}");
                report.AppendLine($"    elapsed: {task.ElapsedTime.TotalSeconds:F0}s of {task.TimeoutSeconds}s timeout");
                report.AppendLine($"    task: {Summarize(task.TaskPayload, 200)}");
            }

            report.AppendLine();
            report.AppendLine($"Recently finished sub-agents: {finished.Count}");
            foreach (var record in finished.OrderBy(r => r.CompletedAt))
            {
                report.AppendLine();
                report.AppendLine($"  • {record.RunId} — {record.Status}");
                report.AppendLine($"    finished: {record.CompletedAt:u}"
                    + (record.Duration.HasValue ? $" after {record.Duration.Value.TotalSeconds:F0}s" : ""));

                if (!string.IsNullOrWhiteSpace(record.Error))
                    report.AppendLine($"    error: {record.Error}");

                if (includeOutput && !string.IsNullOrWhiteSpace(record.Output))
                {
                    report.AppendLine("    output:");
                    report.AppendLine(Indent(record.Output, "      "));
                }
                else if (!string.IsNullOrWhiteSpace(record.Output))
                {
                    report.AppendLine($"    output: {Summarize(record.Output, 160)}");
                }
            }

            return Task.FromResult(ToolResult.Ok(report.ToString().TrimEnd()));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error checking sub-agent status");
            return Task.FromResult(ToolResult.Fail($"Error checking sub-agent status: {ex.Message}"));
        }
    }

    private static ToolResult DescribeSingle(
        string runId,
        List<SubAgentTask> active,
        List<SubAgentCompletionRecord> finished,
        bool includeOutput)
    {
        var task = active.FirstOrDefault(t =>
            string.Equals(t.RunId, runId, StringComparison.OrdinalIgnoreCase));

        if (task != null)
            return ToolResult.Ok($"""
                Sub-agent {task.RunId} is {task.State}.
                Elapsed: {task.ElapsedTime.TotalSeconds:F0}s of a {task.TimeoutSeconds}s timeout.
                Task: {Summarize(task.TaskPayload, 400)}
                """);

        // Newest matching record wins — run IDs are unique, but compare defensively.
        var record = finished
            .Where(r => string.Equals(r.RunId, runId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.CompletedAt)
            .LastOrDefault();

        if (record == null)
            return ToolResult.Ok(
                $"No sub-agent found with run ID '{runId}'. It may have finished long enough ago " +
                "to have been dropped from the retained completion history, or it belongs to a " +
                "different conversation.");

        var detail = new System.Text.StringBuilder();
        detail.AppendLine($"Sub-agent {record.RunId} finished with status {record.Status} at {record.CompletedAt:u}"
            + (record.Duration.HasValue ? $" after {record.Duration.Value.TotalSeconds:F0}s." : "."));

        if (!string.IsNullOrWhiteSpace(record.Error))
            detail.AppendLine($"Error: {record.Error}");

        if (!string.IsNullOrWhiteSpace(record.Output))
        {
            detail.AppendLine("Output:");
            detail.AppendLine(includeOutput ? record.Output : Summarize(record.Output, 400));
        }

        return ToolResult.Ok(detail.ToString().TrimEnd());
    }

    private static string Summarize(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return "(none)";
        var flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    private static string Indent(string text, string prefix) =>
        string.Join(Environment.NewLine,
            text.Replace("\r\n", "\n").Split('\n').Select(line => prefix + line));
}
