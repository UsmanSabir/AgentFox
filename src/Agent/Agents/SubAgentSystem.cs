using AgentFox.Models;
using AgentFox.Memory;
using AgentFox.Runtime;
using AgentFox.Tools;
using AgentFox.LLM;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentFox.Agents;

// ─────────────────────────────────────────────────────────────────────────────
// Interfaces
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Manages agent execution. Delegates to an <see cref="IAgentExecutor"/> that
/// can be swapped at runtime (e.g. to upgrade from simulated to LLM-backed).
/// </summary>
public interface IAgentRuntime
{
    /// <summary>Execute an agent command (supports per-command model overrides).</summary>
    Task<AgentResult> ExecuteAsync(AgentCommand command, CancellationToken ct = default);

    /// <summary>Spawn a lightweight internal sub-agent (used by the integration layer).</summary>
    Agent SpawnSubAgent(Agent parent, AgentConfig config);

    /// <summary>Swap the executor after construction (called once FoxAgent is available).</summary>
    void SetExecutor(IAgentExecutor executor);

    ToolRegistry ToolRegistry { get; }
    ILogger? Logger { get; set; }
}

/// <summary>
/// Executes a single <see cref="AgentCommand"/>.
/// Two implementations are provided:
/// <list type="bullet">
///   <item><see cref="FoxAgentExecutor"/> — real LLM-backed execution via FoxAgent</item>
///   <item><see cref="SimulatedAgentExecutor"/> — keyword-matching stub for tests / doctor commands</item>
/// </list>
/// </summary>
public interface IAgentExecutor
{
    Task<AgentResult> ExecuteAsync(AgentCommand command, CancellationToken ct = default);
}

// ─────────────────────────────────────────────────────────────────────────────
// Supporting types (kept for SpawnSubAgentTool / FoxAgent.SpawnSubAgent)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Configuration for spawning a sub-agent via FoxAgent.</summary>
public class AgentSpawnConfig
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? SystemPrompt { get; set; }
    public List<ToolDefinition>? Tools { get; set; }
    public int MaxIterations { get; set; } = 10;
    public bool InheritMemory { get; set; } = true;
    public bool InheritTools { get; set; } = true;
    public bool InheritEnabledSkills { get; set; } = true;
    public string? Role { get; set; }
    public List<string>? AdditionalSkills { get; set; }
    public List<string>? ForbiddenSkills { get; set; }
}

/// <summary>Result of a low-level internal sub-agent spawn.</summary>
public class SpawnResult
{
    public bool Success { get; set; }
    public Agent? Agent { get; set; }
    public string? Error { get; set; }
}

// ─────────────────────────────────────────────────────────────────────────────
// DefaultAgentRuntime
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Default runtime: delegates execution to an <see cref="IAgentExecutor"/> and
/// exposes <see cref="SetExecutor"/> so Program.cs can inject a real LLM executor
/// once FoxAgent has been built.
/// </summary>
public class DefaultAgentRuntime : IAgentRuntime
{
    private readonly ToolRegistry _toolRegistry;
    private IAgentExecutor _executor;

    public ToolRegistry ToolRegistry => _toolRegistry;
    public ILogger? Logger { get; set; }

    public DefaultAgentRuntime(ToolRegistry toolRegistry, IAgentExecutor? executor = null, ILogger? logger = null)
    {
        _toolRegistry = toolRegistry;
        Logger = logger;
        _executor = executor ?? new SimulatedAgentExecutor(toolRegistry, logger);
    }

    /// <summary>Replace the executor. Call this after FoxAgent is constructed.</summary>
    public void SetExecutor(IAgentExecutor executor) => _executor = executor;

    public Task<AgentResult> ExecuteAsync(AgentCommand command, CancellationToken ct = default)
    {
        Logger?.LogInformation("Runtime executing command for session '{Session}'", command.SessionKey);
        return _executor.ExecuteAsync(command, ct);
    }

    /// <summary>Spawn a lightweight internal Agent (used by the integration layer).</summary>
    public Agent SpawnSubAgent(Agent parent, AgentConfig config)
    {
        var agent = new Agent
        {
            Config = config,
            Parent = parent,
            Status = AgentStatus.Idle
        };

        if (parent.Memory != null)
            agent.Memory = new ShortTermMemory();

        foreach (var tool in parent.Config.Tools.Where(t => !agent.Config.Tools.Any(x => x.Name == t.Name)))
            agent.Config.Tools.Add(tool);

        parent.SubAgents.Add(agent);
        Logger?.LogInformation("Spawned sub-agent '{SubAgent}' from '{Parent}'", config.Name, parent.Config.Name);
        return agent;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// FoxAgentExecutor  (real LLM-backed execution)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// LLM-backed executor. Uses the parent <see cref="FoxAgent"/> for commands
/// with no model override; builds a fresh sub-agent when <see cref="AgentCommand.Model"/>
/// specifies a different model.
/// </summary>
public class FoxAgentExecutor : IAgentExecutor
{
    private readonly FoxAgent _defaultAgent;
    private readonly Func<IChatClient, FoxAgent> _agentFactory;
    private readonly Func<string, IChatClient?> _modelResolver;
    private readonly ILogger<FoxAgentExecutor>? _logger;
    private readonly ConversationCheckpointService? _checkpointService;

    /// <param name="defaultAgent">Parent FoxAgent used when no model override is set.</param>
    /// <param name="agentFactory">
    ///     Factory that builds a fresh FoxAgent wired to a specific <see cref="IChatClient"/>.
    ///     All other settings (tools, memory, session store) are shared with the parent.
    /// </param>
    /// <param name="modelResolver">
    ///     Resolves a model name / named config key to an <see cref="IChatClient"/>.
    ///     Return null to fall back to the default agent.
    /// </param>
    /// <param name="checkpointService">
    ///     Optional service used to restore a session from a checkpoint before
    ///     execution when <see cref="AgentCommand.ResumeFromCheckpoint"/> is set.
    /// </param>
    public FoxAgentExecutor(
        FoxAgent defaultAgent,
        Func<IChatClient, FoxAgent> agentFactory,
        Func<string, IChatClient?> modelResolver,
        ILogger<FoxAgentExecutor>? logger = null,
        ConversationCheckpointService? checkpointService = null)
    {
        _defaultAgent = defaultAgent;
        _agentFactory = agentFactory;
        _modelResolver = modelResolver;
        _logger = logger;
        _checkpointService = checkpointService;
    }

    public async Task<AgentResult> ExecuteAsync(AgentCommand command, CancellationToken ct = default)
    {
        var agent = ResolveAgent(command);
        _logger?.LogInformation(
            "Sub-agent executing: session='{Session}', model='{Model}'",
            command.SessionKey, command.Model ?? "default");

        // Restore session to a specific checkpoint before running when requested
        // (e.g. startup recovery or user-triggered /checkpoint restore).
        if (command.ResumeFromCheckpoint != null && _checkpointService != null)
        {
            _logger?.LogInformation(
                "Restoring session '{Session}' from checkpoint '{CheckpointId}' before execution",
                command.SessionKey, command.ResumeFromCheckpoint.Info.CheckpointId);
            await _checkpointService.RestoreCheckpointAsync(command.SessionKey, command.ResumeFromCheckpoint, ct)
                .ConfigureAwait(false);
        }

        return await agent.ProcessAsync(command.Message, command.SessionKey, command.Streaming, ct);
    }

    private FoxAgent ResolveAgent(AgentCommand command)
    {
        if (string.IsNullOrEmpty(command.Model))
            return _defaultAgent;

        var client = _modelResolver(command.Model);
        if (client == null)
        {
            _logger?.LogWarning("Model '{Model}' could not be resolved; using default", command.Model);
            return _defaultAgent;
        }

        return _agentFactory(client);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// SimulatedAgentExecutor  (test / doctor commands)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Keyword-matching stub executor. Does NOT call an LLM.
/// Useful for health-check / doctor commands and unit tests where a real LLM
/// is not available or not desirable.
/// </summary>
public class SimulatedAgentExecutor : IAgentExecutor
{
    private readonly ToolRegistry _toolRegistry;
    private readonly ILogger? _logger;

    public SimulatedAgentExecutor(ToolRegistry toolRegistry, ILogger? logger = null)
    {
        _toolRegistry = toolRegistry;
        _logger = logger;
    }

    public Task<AgentResult> ExecuteAsync(AgentCommand command, CancellationToken ct = default)
    {
        _logger?.LogInformation("[Simulated] Executing command for session '{Session}'", command.SessionKey);

        var response = BuildResponse(command.Message);

        return Task.FromResult(new AgentResult
        {
            Success = true,
            Output = response
        });
    }

    private string BuildResponse(string message)
    {
        var lower = message.ToLowerInvariant();

        if (lower.Contains("status") || lower.Contains("doctor") || lower.Contains("health"))
        {
            var tools = _toolRegistry.GetAll();
            var sample = string.Join(", ", tools.Take(5).Select(t => t.Name));
            var extra = tools.Count > 5 ? $" (+{tools.Count - 5} more)" : string.Empty;
            return $"[Simulated] Agent healthy. Tools registered: {tools.Count} — {sample}{extra}";
        }

        if (lower.Contains("list") && lower.Contains("tool"))
        {
            var tools = _toolRegistry.GetAll();
            var list = string.Join("\n", tools.Select(t => $"  • {t.Name}: {t.Description}"));
            return $"[Simulated] Available tools ({tools.Count}):\n{list}";
        }

        return $"[Simulated] Received: \"{message}\".\n" +
               "This is the SimulatedAgentExecutor — configure a real LLM to get actual responses.";
    }
}

/// <summary>
/// Simulated agent response (kept for internal use within the simulated executor).
/// </summary>
public class AgentResponse
{
    public string Content { get; set; } = string.Empty;
    public List<ToolCall> ToolCalls { get; set; } = [];
}
