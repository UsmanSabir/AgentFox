using AgentFox.Plugins.Interfaces;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PageAgent.Config;
using PageAgent.Core;

namespace PageAgent.Tools;

/// <summary>
/// AgentFox tool that exposes the autonomous browser agent to the LLM.
/// The agent receives a plain-English goal and drives a real browser to satisfy it,
/// returning a step-by-step execution log plus collected knowledge.
///
/// Registered via <see cref="BrowserAgentModule"/>.
/// </summary>
public sealed class BrowseWebTool : BaseTool
{
    private readonly IChatClient _chatClient;
    private readonly IOptions<BrowserAgentOptions> _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<BrowseWebTool> _logger;

    public override string Name => "browse_web";

    public override string Description =>
        "Autonomously browse the web to research a topic, find documentation, " +
        "or extract information from web pages. Accepts a natural-language goal " +
        "and returns a structured report with collected facts and the steps taken. " +
        "Use this when you need up-to-date information from the internet.";

    public override Dictionary<string, ToolParameter> Parameters => new()
    {
        ["goal"] = new ToolParameter
        {
            Type = "string",
            Description =
                "What to find or accomplish on the web. " +
                "Be specific: include keywords, desired output format, and any constraints. " +
                "Example: \"Find the official C# documentation for async/await and summarise the key points.\"",
            Required = true,
            Example = "Find how to use Microsoft Page Agent in C#",
        },
        ["max_steps"] = new ToolParameter
        {
            Type = "number",
            Description =
                "Maximum number of browser actions allowed (default: uses config value, typically 15). " +
                "Increase for complex multi-page research tasks.",
            Required = false,
            Default = 0,
            Minimum = 1,
            Maximum = 30,
        },
        ["headless"] = new ToolParameter
        {
            Type = "boolean",
            Description =
                "Whether to run the browser invisibly (default: uses config value, typically true). " +
                "Set to false to watch the browser navigate in real time.",
            Required = false,
            Default = null,
        },
    };

    public override string? ToolVersion => "1.0.0";

    public BrowseWebTool(
        IChatClient chatClient,
        IOptions<BrowserAgentOptions> options,
        ILoggerFactory loggerFactory)
    {
        _chatClient = chatClient;
        _options = options;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<BrowseWebTool>();
    }

    protected override async Task<ToolResult> ExecuteInternalAsync(
        Dictionary<string, object?> arguments)
    {
        var goal = arguments["goal"]?.ToString();
        if (string.IsNullOrWhiteSpace(goal))
            return ToolResult.Fail("Parameter 'goal' is required and must not be empty.");

        // Allow per-invocation overrides without mutating the shared options
        var effectiveOptions = BuildEffectiveOptions(arguments);

        _logger.LogInformation("BrowseWebTool invoked. Goal: {Goal}", goal);

        var planner = new LlmPlanner(
            _chatClient,
            _loggerFactory.CreateLogger<LlmPlanner>());

        var agent = new AutonomousPageAgent(
            planner,
            effectiveOptions,
            _loggerFactory);

        using var cts = new CancellationTokenSource(
            TimeSpan.FromMinutes(effectiveOptions.Value.RunTimeoutMinutes + 1));

        try
        {
            var report = await agent.RunAsync(goal, cts.Token);
            _logger.LogInformation("BrowseWebTool completed successfully.");
            return ToolResult.Ok(report);
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Fail(
                $"Browse operation timed out after {effectiveOptions.Value.RunTimeoutMinutes} minutes.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BrowseWebTool failed for goal: {Goal}", goal);
            return ToolResult.Fail($"Browser agent error: {ex.Message}");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns an <see cref="IOptions{T}"/> wrapper whose value reflects any
    /// per-call overrides from the tool arguments without modifying shared state.
    /// </summary>
    private IOptions<BrowserAgentOptions> BuildEffectiveOptions(
        Dictionary<string, object?> arguments)
    {
        // Clone the current options so we can safely mutate
        var opts = new BrowserAgentOptions
        {
            Headless = _options.Value.Headless,
            MaxSteps = _options.Value.MaxSteps,
            MaxExtractLength = _options.Value.MaxExtractLength,
            MaxLinks = _options.Value.MaxLinks,
            MaxHeadings = _options.Value.MaxHeadings,
            NavigationTimeoutMs = _options.Value.NavigationTimeoutMs,
            RetryAttempts = _options.Value.RetryAttempts,
            RunTimeoutMinutes = _options.Value.RunTimeoutMinutes,
            SearchEngineUrl = _options.Value.SearchEngineUrl,
            FallbackSearchEngineUrl = _options.Value.FallbackSearchEngineUrl,
            ViewportWidth = _options.Value.ViewportWidth,
            ViewportHeight = _options.Value.ViewportHeight,
        };

        if (arguments.TryGetValue("max_steps", out var maxStepsRaw))
        {
            var maxSteps = Convert.ToInt32(maxStepsRaw);
            if (maxSteps > 0)
                opts.MaxSteps = maxSteps;
        }

        if (arguments.TryGetValue("headless", out var headlessRaw) && headlessRaw is not null)
            opts.Headless = Convert.ToBoolean(headlessRaw);

        return Microsoft.Extensions.Options.Options.Create(opts);
    }
}
