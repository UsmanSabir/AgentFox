using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PageAgent.Config;
using PageAgent.Models;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace PageAgent.Core;

/// <summary>
/// Orchestrates the plan → act → remember loop until the goal is reached
/// or the configured maximum number of steps is exhausted.
///
/// Each call to <see cref="RunAsync"/> is fully isolated: it creates its own
/// browser session, memory, and step history, then disposes the browser when done.
/// </summary>
public sealed class AutonomousPageAgent
{
    private readonly LlmPlanner _planner;
    private readonly IOptions<BrowserAgentOptions> _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<AutonomousPageAgent> _logger;

    private static readonly JsonSerializerOptions _prettyJson =
        new() { WriteIndented = true };

    public AutonomousPageAgent(
        LlmPlanner planner,
        IOptions<BrowserAgentOptions> options,
        ILoggerFactory loggerFactory)
    {
        _planner = planner;
        _options = options;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<AutonomousPageAgent>();
    }

    /// <summary>
    /// Runs the autonomous browsing loop for the given <paramref name="goal"/>.
    /// Returns a structured text report of all steps taken and knowledge gathered.
    /// </summary>
    /// <param name="goal">Natural-language description of what to find or do.</param>
    /// <param name="ct">Cancellation token (also enforces <c>RunTimeoutMinutes</c>).</param>
    public async Task<string> RunAsync(string goal, CancellationToken ct = default)
    {
        var opts = _options.Value;

        // Merge caller token with a per-run timeout
        using var timeoutCts = new CancellationTokenSource(
            TimeSpan.FromMinutes(opts.RunTimeoutMinutes));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var runCt = linkedCts.Token;

        _logger.LogInformation("=== Autonomous Page Agent starting ===");
        _logger.LogInformation("Goal : {Goal}", goal);
        _logger.LogInformation("Limit: {Max} steps, {Min} min timeout", opts.MaxSteps, opts.RunTimeoutMinutes);

        var memory = new AgentMemory();
        var history = new List<StepResult>();
        var executionLog = new StringBuilder();

        // Start with an empty page state; first planner call will decide to search or open
        var currentPage = new PageAnalysis();

        await using var browser = new PageActorClient(
            _options, _loggerFactory.CreateLogger<PageActorClient>());

        await browser.InitializeAsync(runCt);

        var runSw = Stopwatch.StartNew();

        for (int step = 1; step <= opts.MaxSteps; step++)
        {
            runCt.ThrowIfCancellationRequested();

            _logger.LogInformation("── Step {Step}/{Max} ──", step, opts.MaxSteps);

            // ── 1. Plan ──────────────────────────────────────────────────────
            AgentAction action;
            try
            {
                action = await _planner.DecideNextActionAsync(
                    goal, history, currentPage, memory, runCt);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Planner threw an unexpected error at step {Step}", step);
                action = new AgentAction { Action = "analyze", Reason = "Planner error — retrying" };
            }

            _logger.LogInformation("Decision: {Action}", action);
            executionLog.AppendLine($"[Step {step:D2}] {action}");

            // ── 2. Check for termination ─────────────────────────────────────
            if (action.Action.Equals("done", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Goal achieved at step {Step}.", step);
                return BuildReport(goal, history, memory, executionLog, action.Reason, completed: true);
            }

            // ── 3. Execute action ────────────────────────────────────────────
            var stepSw = Stopwatch.StartNew();
            var (output, success, error) = await ExecuteActionAsync(browser, action, memory, runCt);
            stepSw.Stop();

            var stepResult = new StepResult
            {
                Step = step,
                Action = action,
                Output = output,
                Success = success,
                Error = error,
                Elapsed = stepSw.Elapsed,
            };
            history.Add(stepResult);

            _logger.LogInformation("Result ({Elapsed:F1}s): {Status} — {Preview}",
                stepSw.Elapsed.TotalSeconds,
                success ? "OK" : "FAIL",
                (success ? output : error ?? "").Length > 120
                    ? (success ? output : error ?? "")[..120] + "…"
                    : (success ? output : error ?? ""));

            // ── 4. Update memory with extracted knowledge ────────────────────
            if (success && output.Length > 0)
            {
                var preview = output.Length > 200 ? output[..200] + "…" : output;
                var note = $"[{action.Action}] {preview}";
                memory.AddNote(note);
            }

            // ── 5. Refresh page snapshot after navigating actions ────────────
            if (success && action.Action is "search" or "open" or "click")
            {
                currentPage = await browser.AnalyzePageAsync(runCt);
                if (!currentPage.IsEmpty)
                    memory.MarkVisited(currentPage.Url);
            }
            else if (success && action.Action == "analyze")
            {
                // The action itself returned a serialised PageAnalysis; re-parse into struct
                currentPage = await browser.AnalyzePageAsync(runCt);
            }
        }

        _logger.LogWarning("Reached max steps ({Max}) without completing goal.", opts.MaxSteps);
        return BuildReport(goal, history, memory, executionLog, reason: null, completed: false);
    }

    // ── Action dispatcher ────────────────────────────────────────────────────

    private async Task<(string output, bool success, string? error)> ExecuteActionAsync(
        PageActorClient browser,
        AgentAction action,
        AgentMemory memory,
        CancellationToken ct)
    {
        try
        {
            var output = action.Action.ToLowerInvariant() switch
            {
                "search" when !string.IsNullOrWhiteSpace(action.Query)
                    => await browser.SearchAsync(action.Query, ct),

                "open" when !string.IsNullOrWhiteSpace(action.Url)
                    => await browser.OpenAsync(action.Url, ct),

                "click" when !string.IsNullOrWhiteSpace(action.Selector)
                    => await browser.SmartClickAsync(action.Selector, ct),

                "analyze"
                    => SerialiseAnalysis(await browser.AnalyzePageAsync(ct)),

                "extract"
                    => await browser.ExtractContentAsync(ct),

                _   => $"Unknown or incomplete action: {action.Action}"
            };

            return (output, true, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Action '{Action}' failed: {Error}", action.Action, ex.Message);
            return (string.Empty, false, ex.Message);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string SerialiseAnalysis(PageAnalysis page)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Title: {page.Title}");
        sb.AppendLine($"URL  : {page.Url}");

        if (page.Headings.Count > 0)
        {
            sb.AppendLine("Headings:");
            page.Headings.ForEach(h => sb.AppendLine($"  • {h}"));
        }

        if (page.Links.Count > 0)
        {
            sb.AppendLine("Links:");
            page.Links.Take(10).ToList().ForEach(l => sb.AppendLine($"  • \"{l.Text}\" → {l.Href}"));
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildReport(
        string goal,
        List<StepResult> history,
        AgentMemory memory,
        StringBuilder executionLog,
        string? reason,
        bool completed)
    {
        var sb = new StringBuilder();

        sb.AppendLine(completed
            ? "✓ Goal achieved."
            : "⚠ Max steps reached — goal may be partially satisfied.");
        sb.AppendLine();
        sb.AppendLine($"Goal  : {goal}");
        sb.AppendLine($"Steps : {history.Count}");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(reason))
        {
            sb.AppendLine("Summary:");
            sb.AppendLine(reason);
            sb.AppendLine();
        }

        if (memory.Notes.Count > 0)
        {
            sb.AppendLine("Collected knowledge:");
            foreach (var note in memory.Notes)
                sb.AppendLine($"  • {note}");
            sb.AppendLine();
        }

        sb.AppendLine("Execution log:");
        sb.Append(executionLog);

        return sb.ToString().TrimEnd();
    }
}
