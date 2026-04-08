using AgentFox.LLM;
using AgentFox.MCP;
using AgentFox.Memory;
using AgentFox.Models;
using AgentFox.Sessions;
using AgentFox.Skills;
using AgentFox.Tools;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentFox.Plugins.Interfaces;
using SystemPromptBuilder = AgentFox.LLM.SystemPromptBuilder;

namespace AgentFox.Agents;

/// <summary>
/// Configuration for a model (used in Models section of appsettings)
/// </summary>
public class ModelConfig
{
    public string Provider { get; set; } = "Ollama";
    public string Model { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string? ApiKey { get; set; }
    public int TimeoutSeconds { get; set; } = 3600;
}

/// <summary>
/// Configuration for agent conversation compaction
/// </summary>
public class CompactionConfig
{
    /// <summary>
    /// Enable compaction (default: false)
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Token threshold to trigger compaction (default: 2000)
    /// </summary>
    public int TriggerThreshold { get; set; } = 2000;

    /// <summary>
    /// Token threshold for tool result compaction (default: 1000)
    /// </summary>
    public int ToolResultThreshold { get; set; } = 1000;

    /// <summary>
    /// Token threshold for summarization compaction (default: 6000)
    /// </summary>
    public int SummarizationThreshold { get; set; } = 6000;

    /// <summary>
    /// Token threshold for truncation compaction (default: 8000)
    /// </summary>
    public int TruncationThreshold { get; set; } = 8000;

    /// <summary>
    /// Model key from Models section to use for summarization (optional - uses main chat client if not set)
    /// </summary>
    public string? SummarizationModelKey { get; set; }

    /// <summary>
    /// Internal: Custom chat client for summarization (set by builder)
    /// </summary>
    internal IChatClient? _summarizationChatClient;

    /// <summary>
    /// Internal: Model config for summarization (set from Models section)
    /// </summary>
    internal ModelConfig? _summarizationModelConfig;
}

/// <summary>
/// Main agent class that orchestrates all functionality
/// </summary>
public class FoxAgent
{
    private readonly Agent _agent;
    private readonly ChatClientAgent _chatAgent;
    private readonly ILogger<FoxAgent>? _logger;
    private WorkspaceManager _workspaceManager;

    public string Id => _agent.Config.Id;
    public string Name => _agent.Config.Name;
    public IMemory Memory => _agent.Memory;
    public IConversationStore ConversationStore => _agent.ConversationStore;
    public AgentStatus Status => _agent.Status;
    public List<Agent> SubAgents => _agent.SubAgents;

    /// <summary>
    /// Skills enabled for this agent (used for capability-based routing)
    /// </summary>
    public List<Skill> EnabledSkills { get; set; } = new();

    /// <summary>
    /// Agent role for permission and resource limit checking
    /// </summary>
    public string Role { get; set; } = "default";

    /// <summary>
    /// Optional session manager. When set, conversation IDs are resolved through it,
    /// enabling per-channel persistence, idle archiving, and /new /reset support.
    /// </summary>
    public SessionManager? SessionManager { get; set; }

    /// <summary>
    /// Runtime prompt contributors. Add <see cref="IPromptContributor"/> instances here
    /// to inject dynamic content (e.g. connected MCP servers, active plugins) into the
    /// system prompt before every LLM call without rebuilding the agent.
    /// </summary>
    public PromptContributorRegistry PromptContributors { get; }

    public FoxAgent(ChatClientAgent agent, AgentConfig config, IConversationStore store, string defaultConversationId, WorkspaceManager workspaceManager, PromptContributorRegistry promptContributors, ILogger<FoxAgent>? logger = null)
    {
        _agent = new Agent
        {
            Config = config,
            Status = AgentStatus.Idle,
            ConversationStore = store,
            DefaultConversationId = defaultConversationId
        };
        _chatAgent = agent;
        _workspaceManager = workspaceManager;
        PromptContributors = promptContributors;
        _logger = logger;
    }

    /// <summary>
    /// Configure memory for the agent
    /// </summary>
    public FoxAgent WithMemory(IMemory memory)
    {
        _agent.Memory = memory;
        return this;
    }

    /// <summary>
    /// Configure hybrid memory (short-term + long-term)
    /// </summary>
    public FoxAgent WithHybridMemory(int shortTermSize = 50, string? longTermPath = null)
    {
        _agent.Memory = new HybridMemory(shortTermSize, longTermPath);
        return this;
    }

    /// <summary>
    /// Execute a task with the agent.
    /// Internal: callers outside this assembly should route through ICommandQueue (Main lane).
    /// </summary>
    internal async Task<AgentResult> ExecuteAsync(string task)
    {
        _logger?.LogInformation("Agent '{AgentName}' executing task: {Task}", Name, task.Length > 100 ? task[..100] + "..." : task);

        // Populate agent's enabled skills if empty
        if (EnabledSkills.Count == 0 && _agent.Config.SkillRegistry != null)
        {
            _agent.EnabledSkills.AddRange(_agent.Config.SkillRegistry.GetAll());
            _logger?.LogInformation("Auto-populated agent '{AgentName}' with {SkillCount} available skills", Name, _agent.EnabledSkills.Count);
        }
        var conversationId = SessionManager != null
            ? SessionManager.GetOrCreateConsoleSession(_agent.Config.Id)
            : _agent.DefaultConversationId;
        return await ProcessAsync(task, conversationId);
    }

    /// <summary>
    /// Process a task in a specific conversation session.
    /// Internal: external callers should route through ICommandQueue; only
    /// FoxAgentExecutor and SpawnSubAgentTool use this directly (both same assembly).
    /// </summary>
    internal async Task<AgentResult> ProcessAsync(string task, string? conversationId = null, StreamingCallbacks? streaming = null, CancellationToken cancellationToken = default)
    {
        conversationId ??= Guid.NewGuid().ToString("N");

        // Handle /new and /reset — archive current session and start a fresh one
        if (SessionManager != null && SessionManager.IsResetCommand(task))
        {
            var newId = SessionManager.ResetSession(conversationId);
            _logger?.LogInformation("Session reset by user command: {Old} → {New}", conversationId, newId);
            return new AgentResult { Success = true, Output = $"Session reset. Starting fresh (session: {newId})." };
        }

        _logger?.LogInformation("Agent '{AgentName}' processing task in conversation {ConversationId}", Name, conversationId);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        double TimeoutSeconds = 3600;
        cts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
        var timeoutToken = cts.Token;

        try
        {
            var agent = _chatAgent;

            // Retrieve the cached session. On a cache miss (first call or after restart),
            // create a fresh session and restore any persisted messages from disk so the
            // ChatHistoryProvider sees the full prior history before RunAsync is called.
            var session = ConversationStore.GetSession(conversationId);
            if (session == null)
            {
                _logger?.LogDebug("Creating new conversation session for {ConversationId}", conversationId);
                session = await agent.CreateSessionAsync(cancellationToken);
                session.StateBag.SetValue("ConversationId", conversationId);
                session.StateBag.SetValue("CreatedAt", DateTime.UtcNow.ToString("O"));
                await ConversationStore.RestoreAsync(conversationId, session);
                ConversationStore.SaveSession(conversationId, session);
            }

            var runOptions = new AgentRunOptions();
            //TODO: Look into prompt caching
            //runOptions.AdditionalProperties = new AdditionalPropertiesDictionary { { "prompt_cache_key", "my-shared-context-v1" } };

            // Proactive recall: inject relevant long-term memories as context preamble
            var memoryContext = await BuildMemoryContextAsync(_agent.Memory, task);
            var augmentedTask = string.IsNullOrEmpty(memoryContext) ? task : memoryContext + task;

            // Write the original user message to a sidecar .pending file before the LLM call.
            // If the process crashes mid-response, the pending file lets startup recovery
            // detect the interrupted task and offer to resume it.
            // We persist `task` (not `augmentedTask`) so the memory preamble is rebuilt fresh on retry.
            (ConversationStore as Memory.MarkdownSessionStore)?.PersistIncomingUserMessage(conversationId, task);

            string responseText;

            if (streaming != null)
            {
                // Streaming path: forward tokens to the caller as they arrive (console/terminal).
                // RunStreamingAsync handles the full agentic loop (tool calls, retries) just like
                // RunAsync, but yields ChatResponseUpdate chunks as the model produces them.
                if (streaming.OnStart != null)
                    await streaming.OnStart();

                var sb = new StringBuilder();
                try
                {
                    await foreach (var update in agent.RunStreamingAsync(augmentedTask, session, options: runOptions, cancellationToken: timeoutToken))
                    {
                        //foreach (var content in update.Contents)
                        //{
                        //    if (content.GetType() != typeof(TextContent) && content.GetType() != typeof(TextReasoningContent) && content.GetType() != typeof(UsageContent))
                        //    {
                        //        Console.WriteLine($"Streaming content type {content.GetType()}");
                        //    }
                        //}
                        if (streaming.OnReasoning != null)
                            foreach (var content in update.Contents.OfType<TextReasoningContent>())
                            {
                                if (!string.IsNullOrEmpty(content.Text))
                                {
                                    await streaming.OnReasoning(content.Text);
                                }
                            }
                        if (streaming.OnToken != null)
                            foreach (var content in update.Contents.OfType<TextContent>())
                            {
                                if (!string.IsNullOrEmpty(content.Text))
                                {
                                    sb.Append(content.Text);
                                    await streaming.OnToken(content.Text);
                                }
                            }
                    }
                }
                finally
                {
                    // Signal streaming complete *before* any post-processing (session save,
                    // logging, etc.). This releases the AnsiConsole.Live exclusive context so
                    // subsequent console writes (e.g. from loggers) do not deadlock.
                    if (streaming.OnComplete != null)
                        await streaming.OnComplete();
                }
                responseText = sb.Length > 0 ? sb.ToString() : "I apologize, but I wasn't able to generate a response.";
            }
            else
            {
                // Non-streaming path: channels, CLI, sub-agents — wait for the full response.
                var response = await agent.RunAsync(augmentedTask, session, options: runOptions, cancellationToken: timeoutToken);
                responseText = response.Text ?? "I apologize, but I wasn't able to generate a response.";
            }

            // Persist updated session metadata (e.g. lastActiveAt) after each turn.
            ConversationStore.SaveSession(conversationId, session);

            // Turn completed successfully — remove the pending marker.
            (ConversationStore as MarkdownSessionStore)?.ClearPendingUserMessage(conversationId);

            // Keep the session alive in the session manager
            SessionManager?.TouchSession(conversationId);
            _logger?.LogInformation("Agent '{AgentName}' completed task in conversation {ConversationId}", Name, conversationId);

            var result = new AgentResult { Success = true, Output = responseText };
            return result;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogWarning("Agent '{AgentName}' task timed out after {Timeout} seconds", Name, TimeoutSeconds);
            SessionManager?.MarkAborted(conversationId, "timeout");
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Agent '{AgentName}' failed to process task in conversation {ConversationId}", Name, conversationId);
            SessionManager?.MarkAborted(conversationId, ex.Message);
            throw;
        }
    }


    /// <summary>
    /// Search long-term memory for entries relevant to the current task and format
    /// them as a context preamble to prepend to the user message.
    /// </summary>
    private static async Task<string> BuildMemoryContextAsync(IMemory? memory, string task)
    {
        if (memory == null) return string.Empty;

        var memories = await memory.SearchAsync(task, limit: 5);
        if (memories.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("[Relevant context from long-term memory:]");
        foreach (var mem in memories)
            sb.AppendLine($"- [{mem.Type}] {mem.Content}");
        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>
    /// Spawn a sub-agent
    /// </summary>
    public FoxAgent SpawnSubAgent(AgentSpawnConfig config)
    {
        var agentConfig = new AgentConfig
        {
            Name = config.Name,
            Description = config.Description,
            SystemPrompt = config.SystemPrompt,
            MaxIterations = config.MaxIterations,
            Tools = config.Tools ?? new List<ToolDefinition>(),
            SkillRegistry = _agent.Config.SkillRegistry
        };

        var subAgent = SpawnSubAgentInternal(_agent, agentConfig);
        var foxSubAgent = new FoxAgent(_chatAgent, subAgent.Config, ConversationStore, Guid.NewGuid().ToString("N"), _workspaceManager, PromptContributors)
        {
            Role = config.Role ?? Role  // Inherit role from parent by default
        };

        // Handle skill inheritance
        if (config.InheritEnabledSkills && EnabledSkills.Any())
        {
            // Copy parent skills
            foxSubAgent.EnabledSkills.AddRange(EnabledSkills);

            // Add additional skills if specified
            if (config.AdditionalSkills?.Any() == true)
            {
                // TODO: Load additional skills from SkillRegistry
            }

            // Remove forbidden skills
            if (config.ForbiddenSkills?.Any() == true)
            {
                foreach (var forbidden in config.ForbiddenSkills)
                {
                    foxSubAgent.DisableSkill(forbidden);
                }
            }
        }

        return foxSubAgent;
    }

    private Agent SpawnSubAgentInternal(Agent parent, AgentConfig config)
    {
        _logger?.LogInformation("Spawning sub-agent '{SubAgentName}' from parent '{ParentName}'", config.Name, parent.Config.Name);

        var agent = new Agent
        {
            Config = config,
            Parent = parent,
            Status = AgentStatus.Idle
        };

        if (parent.Memory != null)
        {
            agent.Memory = new Memory.ShortTermMemory();
            _logger?.LogDebug("Sub-agent '{SubAgentName}' inherited memory from parent", config.Name);
        }

        foreach (var tool in parent.Config.Tools)
        {
            if (!agent.Config.Tools.Any(t => t.Name == tool.Name))
            {
                agent.Config.Tools.Add(tool);
            }
        }

        parent.SubAgents.Add(agent);
        _logger?.LogInformation("Sub-agent '{SubAgentName}' spawned successfully with {ToolCount} tools", config.Name, agent.Config.Tools.Count);

        return agent;
    }

    /// <summary>
    /// Get conversation history
    /// </summary>
    //public List<Message> GetHistory()
    //{
    //    return _conversationStore.GetSession(_agent.DefaultConversationId).StateBag  // _agent.ConversationHistory.ToList();
    //}

    /// <summary>
    /// Clear conversation history
    /// </summary>
    public void ClearHistory()
    {
        _agent.ConversationHistory.Clear();
    }

    /// <summary>
    /// Add a system message to the conversation
    /// </summary>
    //public void AddSystemMessage(string content)
    //{
    //    _agent.ConversationHistory.Insert(0, new Message(MessageRole.System, content));
    //}

    /// <summary>
    /// Get agent info
    /// </summary>
    public AgentInfo GetInfo()
    {
        return new AgentInfo
        {
            Id = _agent.Config.Id,
            Name = _agent.Config.Name,
            Description = _agent.Config.Description,
            Status = _agent.Status,
            SubAgentCount = _agent.SubAgents.Count,
            MessageCount = _agent.ConversationHistory.Count,
            CreatedAt = _agent.CreatedAt,
            LastActiveAt = _agent.LastActiveAt,
            HasMemory = _agent.Memory != null,
            ToolCount = _agent.Config.Tools.Count,
            EnabledSkillCount = EnabledSkills.Count,
            Role = Role
        };
    }

    /// <summary>
    /// Get supported skills as a filter for routing
    /// </summary>
    public SkillFilter GetSupportedSkills()
    {
        var skillNames = EnabledSkills.Select(s => s.Name).ToList();
        var capabilities = EnabledSkills
            .Where(s => s.Metadata != null)
            .SelectMany(s => s.Metadata!.Capabilities)
            .Distinct()
            .ToList();

        return new SkillFilter
        {
            RequiredSkills = skillNames,
            RequiredCapabilities = capabilities
        };
    }

    /// <summary>
    /// Enable a skill for this agent
    /// </summary>
    public async Task EnableSkillAsync(Skill skill)
    {
        if (!EnabledSkills.Any(s => s.Name == skill.Name))
        {
            EnabledSkills.Add(skill);
            // Trigger any skill registration hooks
            if (skill is ISkillPlugin plugin)
            {
                var context = new SkillRegistrationContext(
                    new DummyLogger<Skill>(),
                    null,
                    null);
                await plugin.OnRegisterAsync(context);
            }
        }
    }

    /// <summary>
    /// Disable a skill for this agent
    /// </summary>
    public void DisableSkill(string skillName)
    {
        EnabledSkills.RemoveAll(s => s.Name == skillName);
    }

    /// <summary>
    /// Get all skill capabilities for this agent
    /// </summary>
    public List<string> GetSkillCapabilities()
    {
        return EnabledSkills
            .Where(s => s.Metadata != null)
            .SelectMany(s => s.Metadata!.Capabilities)
            .Distinct()
            .ToList();
    }
}

/// <summary>
/// Agent information
/// </summary>
public class AgentInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public AgentStatus Status { get; set; }
    public int SubAgentCount { get; set; }
    public int MessageCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastActiveAt { get; set; }
    public bool HasMemory { get; set; }
    public int ToolCount { get; set; }
    public int EnabledSkillCount { get; set; }
    public string Role { get; set; } = "default";
}

/// <summary>
/// Builder for creating agents
/// </summary>
public class AgentBuilder
{
    private readonly ToolRegistry _toolRegistry;
    private readonly AgentConfig _config = new();
    private IMemory? _memory;
    private SkillRegistry? _skillRegistry = null;
    private MCPClient? _mcpClient;
    private IConversationStore? _conversationStore;
    private ILogger<FoxAgent>? _logger;
    private IChatClient? _chatClient;
    private CompactionConfig? _compactionConfig;
    private ChatHistoryProvider? _chatHistoryProvider;
    private WorkspaceManager _workspaceManager;
    private SessionManager? _sessionManager;
    private readonly PromptContributorRegistry _promptContributorRegistry = new();
    private SkillRegistry? _pendingSkillsRegistry; // set by WithSkillsRegistry, consumed in Build()

    public AgentBuilder(ToolRegistry toolRegistry)
    {
        _toolRegistry = toolRegistry;
    }

    /// <summary>
    /// Register a prompt contributor that injects dynamic content into the system prompt
    /// before every LLM call. Contributors can also be added at runtime via
    /// <see cref="FoxAgent.PromptContributors"/>.
    /// </summary>
    public AgentBuilder WithPromptContributor(IPromptContributor contributor)
    {
        _promptContributorRegistry.Add(contributor);
        return this;
    }

    public AgentBuilder WithName(string name)
    {
        _config.Name = name;
        return this;
    }

    public AgentBuilder WithDescription(string description)
    {
        _config.Description = description;
        return this;
    }

    public AgentBuilder WithSystemPrompt(string systemPrompt)
    {
        _config.SystemPrompt = systemPrompt;
        return this;
    }

    public AgentBuilder WithMaxIterations(int maxIterations)
    {
        _config.MaxIterations = maxIterations;
        return this;
    }

    public AgentBuilder WithTemperature(double temperature)
    {
        _config.Temperature = temperature;
        return this;
    }

    public AgentBuilder WithMemory(IMemory memory)
    {
        _memory = memory;
        return this;
    }

    public AgentBuilder WithConversationStore(IConversationStore conversationStore)
    {
        _conversationStore = conversationStore;
        return this;
    }
    
    public AgentBuilder WithChatClient(IChatClient chatClient)
    {
        _chatClient = chatClient;
        return this;
    }

    public AgentBuilder WithHybridMemory(int shortTermSize = 50, string? longTermPath = null)
    {
        _memory = new HybridMemory(shortTermSize, longTermPath);
        return this;
    }

    public AgentBuilder WithSkillsRegistry(SkillRegistry skillRegistry)
    {
        _skillRegistry = skillRegistry;
        _config.SkillRegistry = skillRegistry;
        // Auto-register contributor — will surface skills enabled after Build()
        // The build-time skill snapshot is recorded in Build() when the contributor is created.
        _pendingSkillsRegistry = skillRegistry;
        return this;
    }

    public AgentBuilder WithMCPClient(MCPClient mcpClient)
    {
        _mcpClient = mcpClient;
        // Auto-register: injects connected MCP server list into system prompt each turn
        _promptContributorRegistry.Remove("mcp-servers"); // idempotent re-set
        _promptContributorRegistry.Add(new MCPServerContributor(mcpClient));
        return this;
    }

    public AgentBuilder WithLogger(ILogger<FoxAgent> logger)
    {
        _logger = logger;
        return this;
    }

    public AgentBuilder WithWorkspaceManager(WorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
        return this;
    }

    public AgentBuilder WithSessionManager(SessionManager sessionManager)
    {
        _sessionManager = sessionManager;
        return this;
    }

    public AgentBuilder WithHistoryProvider(ChatHistoryProvider chatHistoryProvider)
    {
        _chatHistoryProvider = chatHistoryProvider;
        return this;
    }

    /// <summary>
    /// Configure compaction for the agent (optional - disabled by default)
    /// </summary>
    public AgentBuilder WithCompaction(CompactionConfig? config)
    {
        _compactionConfig = config;
        return this;
    }

    /// <summary>
    /// Enable compaction with default settings
    /// </summary>
    public AgentBuilder WithCompaction()
    {
        _compactionConfig = new CompactionConfig();
        return this;
    }

    /// <summary>
    /// Enable compaction with a custom chat client for summarization (to reduce costs)
    /// </summary>
    public AgentBuilder WithCompaction(IChatClient summarizationChatClient, CompactionConfig? config = null)
    {
        _compactionConfig = config ?? new CompactionConfig();
        _compactionConfig._summarizationChatClient = summarizationChatClient;
        return this;
    }

    /// <summary>
    /// Load compaction configuration from appsettings.json section "Compaction"
    /// </summary>
    public AgentBuilder WithCompactionFromConfig(IConfiguration configuration)
    {
        var compactionSection = configuration.GetSection("Compaction");
        if (compactionSection.Exists())
        {
            var config = new CompactionConfig();
            compactionSection.Bind(config);

            if (config.Enabled)
            {
                // Look up the summarization model from Models section if key is specified
                if (!string.IsNullOrEmpty(config.SummarizationModelKey))
                {
                    var modelsSection = configuration.GetSection("Models");
                    var modelSection = modelsSection.GetSection(config.SummarizationModelKey!);
                    if (modelSection.Exists())
                    {
                        var modelConfig = new ModelConfig();
                        modelSection.Bind(modelConfig);
                        config._summarizationModelConfig = modelConfig;
                        _logger?.LogInformation("Using model '{ModelKey}' for summarization: Provider={Provider}, Model={Model}, BaseUrl={BaseUrl}",
                            config.SummarizationModelKey, modelConfig.Provider, modelConfig.Model, modelConfig.BaseUrl);
                    }
                    else
                    {
                        _logger?.LogWarning("Model key '{ModelKey}' not found in Models section, will use main chat client for summarization",
                            config.SummarizationModelKey);
                    }
                }

                _compactionConfig = config;
            }
            else
            {
                _compactionConfig = null;
            }
        }
        return this;
    }

    #region Helpers

    private string BuildSystemMessage()
    {
        // If a complete system prompt was provided (e.g. built by SystemPromptBuilder in
        // Program.cs with WithTools already applied), return it as-is to avoid a second
        // "AVAILABLE TOOLS:" section being appended here.
        if (_config.SystemPrompt != null)
            return _config.SystemPrompt;

        // No system prompt provided — build one from the default persona + registered tools.
        var builder = new SystemPromptBuilder()
            .WithPersona(SystemPromptConfig.AgentPrompts.BaseAssistant);

        if (_config.Tools.Count > 0)
        {
            var toolNames = _config.Tools
                .Select(t => $"{t.Name}: {t.Description}")
                .ToArray();
            builder.WithTools(toolNames);
            builder.WithToolInstructions(false);
        }

        return builder.Build();
    }

    /// <summary>
    /// Create a separate chat client for summarization using ModelConfig
    /// </summary>
    private IChatClient? CreateSummarizationChatClient(ModelConfig? modelConfig)
    {
        if (modelConfig == null)
        {
            return null;
        }

        try
        {
            _logger?.LogInformation("Creating summarization chat client: Provider={Provider}, Model={Model}, BaseUrl={BaseUrl}",
                modelConfig.Provider, modelConfig.Model, modelConfig.BaseUrl);

            var chatClient = LLMFactory.CreateChatClient(modelConfig);
            return chatClient;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to create summarization chat client, falling back to main client");
            return null;
        }
    }

    /// <summary>
    /// Create a separate chat client for summarization with optional model override (legacy)
    /// </summary>
    private IChatClient? CreateSummarizationChatClient(string? model, string? baseUrl, string? provider)
    {
        if (string.IsNullOrEmpty(model) && string.IsNullOrEmpty(baseUrl))
        {
            return null;
        }

        return CreateSummarizationChatClient(new ModelConfig
        {
            Provider = provider ?? "Ollama",
            Model = model ?? "phi4-mini",
            BaseUrl = baseUrl ?? "http://localhost:11434"
        });
    }

    private List<ToolDefinition> GetAvailableTools()
    {
        var tools = new List<ToolDefinition>();

        // Add configured tools
        tools.AddRange(_toolRegistry.GetDefinitions());

        // Add tools from enabled skills (including Composio toolkit tools)
        var enabledSkills = _skillRegistry?.GetEnabledSkills();
        if (enabledSkills != null)
            foreach (var skill in enabledSkills)
            {
                try
                {
                    var skillTools = skill.GetTools();
                    foreach (var tool in skillTools)
                    {
                        if (tools.Any(t => t.Name.Equals(tool.Name, StringComparison.OrdinalIgnoreCase)))
                            continue;

                        // Convert skill tool to tool definition for the LLM
                        var parameters = new Dictionary<string, Models.ToolParameter>();
                        foreach (var kv in tool.Parameters)
                        {
                            parameters[kv.Key] = new Models.ToolParameter
                            {
                                Type = kv.Value.Type,
                                Description = kv.Value.Description,
                                Required = kv.Value.Required,
                                Default = kv.Value.Default,
                                Pattern = kv.Value.Pattern,
                                MinLength = kv.Value.MinLength,
                                MaxLength = kv.Value.MaxLength,
                                Minimum = kv.Value.Minimum,
                                Maximum = kv.Value.Maximum
                            };
                        }

                        tools.Add(new ToolDefinition
                        {
                            Name = tool.Name,
                            Description = tool.Description,
                            Parameters = parameters
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to get tools from skill {SkillName}", skill.Name);
                }
            }

        return tools;
    }

    /// <summary>
    /// Search for a tool in the agent's enabled skills by name
    /// </summary>
    private ITool? FindToolInAgentSkills(string toolName)
    {
        try
        {
            // Search each enabled skill's tools for a match
            var enabledSkills = _skillRegistry?.GetEnabledSkills();
            if (enabledSkills != null)
                foreach (var skill in enabledSkills)
                {
                    var skillTools = skill.GetTools();
                    var matchedTool = skillTools.FirstOrDefault(t =>
                            t.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase) ||
                            t.Name.EndsWith($":{toolName}") || // Handle toolkit:tool format
                            toolName.EndsWith($":{t.Name}") // Handle reverse format
                    );

                    if (matchedTool != null)
                    {
                        _logger?.LogInformation($"Found tool '{toolName}' in skill '{skill.Name}'");
                        return matchedTool;
                    }
                }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error searching for tool in agent skills");
        }

        return null;
    }

    private async Task<ToolResult> ExecuteToolAsync(string toolName, Dictionary<string, object?> arguments, CancellationToken ct)
    {
        // First, try to get tool from global registry
        var tool = _toolRegistry.Get(toolName);

        // If not found, search in agent's enabled skills (for Composio and other skill-based tools)
        if (tool == null)
        {
            tool = FindToolInAgentSkills(toolName);
        }

        if (tool == null)
        {
            // Log available tools for debugging
            var availableTools = _toolRegistry.GetAll();
            var toolNames = string.Join(", ", availableTools.Select(t => t.Name));
            var enabledSkills = _skillRegistry?.GetEnabledSkills();
            if (enabledSkills != null)
            {
                var skillToolsInfo = string.Join(", ", enabledSkills.SelectMany(s => s.GetTools()).Select(t => t.Name));
                _logger?.LogWarning($"Tool '{toolName}' not found. Global tools: {toolNames}. Skill tools: {skillToolsInfo}");
            }

            return ToolResult.Fail($"Tool not found: {toolName}");
        }

        try
        {
            var result = await tool.ExecuteAsync(arguments);
            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, $"Error executing tool {toolName}");
            return ToolResult.Fail($"Error: {ex.Message}");
        }
    }

    private static JsonElement BuildJsonSchema(ToolDefinition tool)
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var (name, param) in tool.Parameters)
        {
            JsonObject prop = new()
            {
                ["type"] = param.Type
            };

            if (!string.IsNullOrEmpty(param.Description))
                prop["description"] = param.Description;

            if (param.Pattern != null)
                prop["pattern"] = param.Pattern;

            if (param.MinLength.HasValue)
                prop["minLength"] = param.MinLength;

            if (param.MaxLength.HasValue)
                prop["maxLength"] = param.MaxLength;

            if (param.Minimum.HasValue)
                prop["minimum"] = param.Minimum;

            if (param.Maximum.HasValue)
                prop["maximum"] = param.Maximum;

            properties[name] = prop;

            if (param.Required)
                required.Add(name);
        }

        var root = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties
        };

        if (required.Count > 0)
            root["required"] = required;

        return JsonSerializer.SerializeToElement(root);
    }

    private static object? ConvertJsonValue(object? value)
    {
        if (value is JsonElement el)
        {
            return el.ValueKind switch
            {
                JsonValueKind.String => el.GetString(),
                JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                JsonValueKind.Object => JsonSerializer.Deserialize<Dictionary<string, object?>>(el.GetRawText()),
                JsonValueKind.Array => JsonSerializer.Deserialize<List<object?>>(el.GetRawText()),
                _ => el.GetRawText()
            };
        }

        return value;
    }

    private AITool CreateAgentTool(ToolDefinition toolDefinition)
    {
        var toolName = toolDefinition.Name;
        var toolDescription = toolDefinition.Description ?? $"Tool: {toolName}";

        //if (toolDefinition.JsonSchema.ValueKind != JsonValueKind.Undefined && toolDefinition.JsonSchema.ValueKind != JsonValueKind.Null)
        //{
        //    var schemaJson = JsonSerializer.Serialize(toolDefinition.JsonSchema, new JsonSerializerOptions { WriteIndented = false });
        //    toolDescription += $"\nParameters (JSON Schema): {schemaJson}";
        //}

        var schema = BuildJsonSchema(toolDefinition);


        var tool = AIFunctionFactory.Create(
            async (AIFunctionArguments? args, CancellationToken ct) =>
            {
                //var jsonArgs = args is null or { Count: 0 }
                //    ? "{}"
                //    : JsonSerializer.Serialize(args);

                var dict = args?.ToDictionary(k => k.Key, v => ConvertJsonValue(v.Value))
                           ?? new Dictionary<string, object?>();

                var res = await ExecuteToolAsync(toolDefinition.Name, dict, ct);
                return res;
                //return JsonSerializer.Serialize(res);

                //return await InvokeMcpToolAsync(toolName, jsonArgs, ct);
            },
            new AIFunctionFactoryOptions
            {
                Name = toolName,
                //Description = toolDescription+$"\nParameters (JSON Schema): {schema}",
                Description = toolDescription, //if using DynamicSchemaFunction
                //JsonSchemaCreateOptions = new AIJsonSchemaCreateOptions()
                //{
                //    // Dynamically provide descriptions for parameters
                //    ParameterDescriptionProvider=  (parameter) =>
                //    {
                //        if (parameter.Name == "dynamic_id") return "The unique ID from your external system";
                //        return toolDescription; // Fallback to default
                //    }
                //}
            });
        AIFunction executableDynamicFunc = new DynamicSchemaFunction(tool, schema);
        _logger?.LogDebug("Created Agent Framework tool: {ToolName}", toolName);
        return executableDynamicFunc;
        //return AIFunctionFactory.CreateDeclaration(toolName, toolDescription, schema);
        //chat  history
        //https://github.com/microsoft/agent-framework/blob/1b7940c91e045c563faafe11d5b03067f4ea7b16/docs/decisions/0015-agent-run-context.md?plain=1#L36
        return tool;
    }

    #endregion

    public sealed class DynamicSchemaFunction(AIFunction inner, JsonElement customSchema) : DelegatingAIFunction(inner)
    {
        // Return the dynamic schema instead of the one inferred from the delegate
        public override JsonElement JsonSchema => customSchema;
    }
    
    private async Task LoadMainSession(ChatClientAgent agent)
    {
        //// 1. Try to find the existing JSON state in your DB for this fixed key
        //string savedJson = await _myDb.GetSessionByFixedKeyAsync(DefaultKey);

        //if (!string.IsNullOrEmpty(savedJson))
        //{
        //    // 2. Hydrate the session from the saved state
        //    return await agent.DeserializeSessionAsync(savedJson);
        //}

        //// 3. Fallback: If it's the very first run, create a fresh session
        //var newSession = await agent.CreateSessionAsync();

        //// Save the initial state immediately so it's ready for the next restart
        //string initialState = await agent.SerializeSessionAsync(newSession);
        //await _myDb.SaveSessionAsync(DefaultKey, initialState);

    }

    public FoxAgent Build()
    {
        if (string.IsNullOrEmpty(_config.Name))
        {
            _config.Name = "AgentFox-" + Guid.NewGuid().ToString("N")[..8];
        }

        var chatClient = _chatClient;

        var tools = GetAvailableTools();
        if (_config.Tools.Count == 0)
        {
            _config.Tools.AddRange(tools);
        }

        // Snapshot tool names at build time — DynamicAgentMiddleware will only surface
        // tools registered AFTER this point (avoiding duplicates with the static set).
        var baselineToolNames = _toolRegistry.GetAll().Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Register RuntimeSkillsContributor now that we know the build-time skill set.
        // Deferred from WithSkillsRegistry() so we can pass the accurate baseline names.
        if (_pendingSkillsRegistry != null)
        {
            var buildTimeSkillNames = _pendingSkillsRegistry.GetEnabledSkills().Select(s => s.Name);
            _promptContributorRegistry.Remove("runtime-skills"); // idempotent re-set
            _promptContributorRegistry.Add(new RuntimeSkillsContributor(_pendingSkillsRegistry, buildTimeSkillNames));
        }
        //var toolCalls = tools?.Select(t => new
        //{
        //    type = "function",
        //    function = new
        //    {
        //        name = t.Name,
        //        description = t.Description,
        //        parameters = new
        //        {
        //            type = "object",
        //            properties = t.Parameters.ToDictionary(p => p.Key,
        //                p => new { type = p.Value.Type, description = p.Value.Description }),
        //            required = t.Parameters.Where(p => p.Value.Required).Select(p => p.Key)
        //        }
        //    }
        //});

        var agentTools = new List<AITool>();
        
        foreach (var toolDefinition in tools)
        {
            try
            {
                var tool = CreateAgentTool(toolDefinition);
                agentTools.Add(tool);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to create Agent Framework tool for MCP tool: {ToolName}", toolDefinition.Name);
            }
        }

        var systemPrompt = BuildSystemMessage();

        //https://github.com/microsoft/agent-framework/blob/main/dotnet/src/Microsoft.Agents.AI/TextSearchProvider.cs
        var textSearchProviderOptions = new TextSearchProviderOptions()
        {
            SearchTime = TextSearchProviderOptions.TextSearchBehavior.OnDemandFunctionCalling,
            FunctionToolDescription = "Allows searching for additional information to help answer the user question. It uses Long and short term memories along with other available context to find relevant information."
        };
        var textSearchProvider = new TextSearchProvider(SearchLongTermMemory, textSearchProviderOptions);
        
        //var agent = chatClient.AsAIAgent(systemPrompt, tools: agentTools);

        // Define a pipeline with multiple strategies (only if compaction is enabled)
#pragma warning disable MAAI001
        //https://github.com/microsoft/agent-framework/tree/d30103fee6b03e2322dc13d590ef43661692b7c9/dotnet/samples/02-agents/AgentSkills
        //var options = new AgentFileSkillsSourceOptions();
        //var skillsBuilder = new AgentSkillsProviderBuilder()
        //    .UseFileSkills(skillPaths:
        //    [
        //        Path.Combine(AppContext.BaseDirectory, "company-skills"),
        //        Path.Combine(AppContext.BaseDirectory, "team-skills"),
        //    ])
        //    .UseFileScriptRunner(SubprocessScriptRunner.RunAsync)
        //    .UseOptions(o => o.DisableCaching = true)
        //    .Build();

        var agentBuilder = chatClient.AsBuilder();

        // Install dynamic middleware — wraps the IChatClient so every LLM call (streaming
        // and non-streaming) automatically receives up-to-date tools and prompt addons.
        // Capture locals so the closure does not root the entire AgentBuilder.
        var toolRegistry = _toolRegistry;
        var promptRegistry = _promptContributorRegistry;
        agentBuilder.Use(inner => new DynamicAgentMiddleware(
            inner,
            toolRegistry,
            promptRegistry,
            baselineToolNames,
            def => CreateAgentTool(def)));

        if (_compactionConfig != null)
        {
            _logger?.LogInformation("Compaction enabled for agent '{AgentName}' with trigger at {TriggerThreshold} tokens",
                _config.Name, _compactionConfig.TriggerThreshold);

            // Use main chat client for summarization, or create/use a separate one if configured
            IChatClient summarizationClient = chatClient;

            // Check priority: 1) Custom chat client provided directly, 2) Model config from Models section, 3) Legacy string properties
            if (_compactionConfig._summarizationChatClient != null)
            {
                _logger?.LogInformation("Using custom summarization chat client provided directly");
                summarizationClient = _compactionConfig._summarizationChatClient;
            }
            else if (_compactionConfig._summarizationModelConfig != null)
            {
                _logger?.LogInformation("Using model '{ModelKey}' from Models section for summarization",
                    _compactionConfig.SummarizationModelKey);
                var summarizationChatClient = CreateSummarizationChatClient(_compactionConfig._summarizationModelConfig);
                if (summarizationChatClient != null)
                {
                    summarizationClient = summarizationChatClient;
                }
            }

            PipelineCompactionStrategy compactionPipeline = new([
                new ToolResultCompactionStrategy(CompactionTriggers.TokensExceed(_compactionConfig.ToolResultThreshold)),
                new SummarizationCompactionStrategy(summarizationClient,
                    CompactionTriggers.TokensExceed(_compactionConfig.SummarizationThreshold)),
                new TruncationCompactionStrategy(CompactionTriggers.TokensExceed(_compactionConfig.TruncationThreshold))
            ]);

            agentBuilder = agentBuilder.UseAIContextProviders(new CompactionProvider(compactionPipeline));
        }
        else
        {
            _logger?.LogInformation("Compaction disabled for agent '{AgentName}'", _config.Name);
        }

        ChatHistoryProvider chatHistoryProvider =
            _chatHistoryProvider ?? new InMemoryChatHistoryProvider();

        
        //AgentWorkflowBuilder

        // 2. Build the agent with nested ChatOptions
        //TODO: Incorporate AgentToolkit https://github.com/microsoft/agent-governance-toolkit
        var agent = agentBuilder
            .BuildAIAgent(new ChatClientAgentOptions
            {
                Name = _config.Name,
                ChatHistoryProvider = chatHistoryProvider,
                ChatOptions = new ChatOptions // Tools must go here
                {
                    Tools = agentTools,
                    Instructions = systemPrompt
                },
                AIContextProviders = [
                    textSearchProvider
                ],
            });

#pragma warning restore MAAI001

        _logger?.LogInformation("Building FoxAgent '{AgentName}' with {ToolCount} tools", _config.Name, tools.Count);

        
        var foxAgent = new FoxAgent(agent, _config, _conversationStore!, "main", _workspaceManager, _promptContributorRegistry, _logger);

        if (_sessionManager != null)
            foxAgent.SessionManager = _sessionManager;

        // Apply memory configuration if set
        if (_memory != null)
        {
            foxAgent.WithMemory(_memory);
        }

        _logger?.LogInformation("FoxAgent '{AgentName}' built successfully", _config.Name);

        return foxAgent;
    }

    private async Task<IEnumerable<TextSearchProvider.TextSearchResult>> SearchLongTermMemory(string query, CancellationToken token)
    {
        var searchResult = await _memory?.SearchAsync(query)!;
        var textSearchResults = searchResult?.Select(s=> new TextSearchProvider.TextSearchResult()
        {
            Text = s.Content,
            RawRepresentation = s.Content 
        }).ToList();
        return textSearchResults ?? new List<TextSearchProvider.TextSearchResult>();
    }
}

