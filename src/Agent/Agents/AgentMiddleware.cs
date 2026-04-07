using System.Runtime.CompilerServices;
using AgentFox.MCP;
using AgentFox.Models;
using AgentFox.Skills;
using AgentFox.Tools;
using Microsoft.Extensions.AI;

namespace AgentFox.Agents;

/// <summary>
/// Provides dynamic content injected into the agent's system prompt each turn.
/// <para>
/// Implement this to contribute runtime-contextual prompt fragments — e.g. a list of
/// currently connected MCP servers, the active workspace, loaded plugin capabilities,
/// or any context that changes independently of the agent's build.
/// </para>
/// <para>
/// <b>Prompt caching</b>: <see cref="GetFragment"/> is called every turn but results are
/// hashed. If no contributor changes its output, the same <c>Instructions</c> string is
/// reused, keeping the provider's cached prefix warm.
/// </para>
/// </summary>
public interface IPromptContributor
{
    /// <summary>Stable identifier used to track and remove this contributor.</summary>
    string ContributorId { get; }

    /// <summary>
    /// Returns a system prompt fragment for the current turn, or <c>null</c> if there is
    /// nothing to contribute. Keep this fast and free of side-effects.
    /// </summary>
    string? GetFragment();
}

/// <summary>
/// Thread-safe registry for <see cref="IPromptContributor"/> instances.
/// <para>
/// Register contributors here to have their content appended after the agent's static
/// base prompt. The <see cref="DynamicAgentMiddleware"/> reads this registry before every
/// LLM call and rebuilds the addon string only when contributor output has changed.
/// </para>
/// <example>
/// <code>
/// foxAgent.PromptContributors.Add(new ActiveWorkspaceContributor(workspaceManager));
/// foxAgent.PromptContributors.Add(new MCPServerContributor(mcpClient));
/// </code>
/// </example>
/// </summary>
public class PromptContributorRegistry
{
    private readonly List<IPromptContributor> _contributors = new();
    private readonly object _lock = new();

    public void Add(IPromptContributor contributor)
    {
        lock (_lock) _contributors.Add(contributor);
    }

    public void Remove(string contributorId)
    {
        lock (_lock) _contributors.RemoveAll(c => c.ContributorId == contributorId);
    }

    public IReadOnlyList<IPromptContributor> GetAll()
    {
        lock (_lock) return _contributors.ToList().AsReadOnly();
    }
}

/// <summary>
/// IChatClient middleware that dynamically injects newly-registered tools and system
/// prompt fragments before every LLM call — without requiring an agent rebuild.
/// <para>
/// <b>How it works:</b>
/// <list type="bullet">
///   <item><term>Dynamic tools</term>
///     <description>Compares <see cref="ToolRegistry.Version"/> against a cached baseline.
///     When the registry has grown (new MCP server connected, skill enabled at runtime),
///     the new tool definitions are wrapped as <see cref="AITool"/> objects and appended to
///     <see cref="ChatOptions.Tools"/> for that call. Already-known tools are never
///     duplicated.</description></item>
///   <item><term>Prompt addons</term>
///     <description>Collects fragments from all registered <see cref="IPromptContributor"/>
///     instances, hashes them, and appends the combined addon after
///     <see cref="ChatOptions.Instructions"/>. The addon string is rebuilt only when the
///     hash changes, so unchanged content reuses the exact same string — preserving any
///     provider-side prompt-cache entry.</description></item>
/// </list>
/// </para>
/// <para>
/// Installed via <c>IChatClientBuilder.Use(inner => new DynamicAgentMiddleware(...))</c>
/// inside <see cref="AgentBuilder.Build"/>.
/// </para>
/// </summary>
internal sealed class DynamicAgentMiddleware : DelegatingChatClient
{
    private readonly ToolRegistry _toolRegistry;
    private readonly PromptContributorRegistry _promptRegistry;
    private readonly HashSet<string> _baselineToolNames;
    private readonly Func<ToolDefinition, AITool> _toolFactory;

    // Tool injection cache — rebuilt only when Version changes
    private int _cachedToolVersion;
    private List<AITool> _cachedNewTools = [];

    // Prompt addon cache — rebuilt only when contributor output changes
    private string? _cachedAddonHash;
    private string? _cachedAddon;

    internal DynamicAgentMiddleware(
        IChatClient inner,
        ToolRegistry toolRegistry,
        PromptContributorRegistry promptRegistry,
        IEnumerable<string> baselineToolNames,
        Func<ToolDefinition, AITool> toolFactory) : base(inner)
    {
        _toolRegistry = toolRegistry;
        _promptRegistry = promptRegistry;
        _baselineToolNames = new HashSet<string>(baselineToolNames, StringComparer.OrdinalIgnoreCase);
        _toolFactory = toolFactory;
        // Snapshot the version at construction so first-call change detection is accurate
        _cachedToolVersion = toolRegistry.Version;
    }

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => base.GetResponseAsync(messages, PrepareOptions(options), cancellationToken);

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var prepared = PrepareOptions(options);
        await foreach (var update in base.GetStreamingResponseAsync(messages, prepared, cancellationToken))
            yield return update;
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private ChatOptions PrepareOptions(ChatOptions? options)
    {
        var opts = options?.Clone() ?? new ChatOptions();
        InjectDynamicTools(opts);
        InjectPromptAddons(opts);
        return opts;
    }

    /// <summary>
    /// Appends tools registered after the agent was built.
    /// No-op when <see cref="ToolRegistry.Version"/> hasn't changed since the last call.
    /// </summary>
    private void InjectDynamicTools(ChatOptions options)
    {
        var currentVersion = _toolRegistry.Version;
        if (currentVersion != _cachedToolVersion)
        {
            var newDefs = _toolRegistry.GetDefinitions()
                .Where(d => !_baselineToolNames.Contains(d.Name))
                .ToList();

            var built = new List<AITool>(newDefs.Count);
            foreach (var def in newDefs)
            {
                try { built.Add(_toolFactory(def)); }
                catch { /* skip malformed tool definition */ }
            }

            _cachedNewTools = built;
            _cachedToolVersion = currentVersion;
        }

        if (_cachedNewTools.Count > 0)
            options.Tools = [..(options.Tools ?? []), .._cachedNewTools];
    }

    /// <summary>
    /// Appends system prompt fragments from registered contributors.
    /// Rebuilds the addon string only when the combined hash changes.
    /// When unchanged, the identical string reference is reused — preserving
    /// any provider-side prompt-cache entry that prefix-matches Instructions.
    /// </summary>
    private void InjectPromptAddons(ChatOptions options)
    {
        var contributors = _promptRegistry.GetAll();
        if (contributors.Count == 0) return;

        var fragments = contributors
            .Select(c => c.GetFragment())
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .ToList()!;

        if (fragments.Count == 0) return;

        var hash = ComputeHash(fragments);
        if (hash != _cachedAddonHash)
        {
            _cachedAddon = "\n\n" + string.Join("\n\n", fragments);
            _cachedAddonHash = hash;
        }

        options.Instructions = (options.Instructions ?? string.Empty) + _cachedAddon;
    }

    private static string ComputeHash(IEnumerable<string> parts)
    {
        var h = 0;
        foreach (var part in parts)
            h = HashCode.Combine(h, part.GetHashCode(StringComparison.Ordinal));
        return h.ToString();
    }
}

// ── Built-in contributors ────────────────────────────────────────────────────

/// <summary>
/// Injects a section listing currently connected MCP servers and their tool counts
/// into the system prompt. Automatically registered by
/// <see cref="AgentBuilder.WithMCPClient"/>.
/// <para>
/// Fragment is only emitted when at least one server is connected, and only rebuilt
/// when the server list changes — keeping the cached prompt prefix stable across turns.
/// </para>
/// </summary>
public sealed class MCPServerContributor : IPromptContributor
{
    private readonly MCPClient _mcpClient;
    public string ContributorId => "mcp-servers";

    public MCPServerContributor(MCPClient mcpClient) => _mcpClient = mcpClient;

    public string? GetFragment()
    {
        var servers = _mcpClient.GetConnectedServers();
        if (servers.Count == 0) return null;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Connected MCP Servers");
        foreach (var s in servers)
        {
            var toolNames = s.AvailableTools.Count > 0
                ? string.Join(", ", s.AvailableTools.Select(t => t.Name))
                : "no tools";
            sb.AppendLine($"- **{s.Name}** ({s.AvailableTools.Count} tools): {toolNames}");
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// Injects a section listing skills enabled at runtime (after agent build) into the
/// system prompt. Automatically registered by <see cref="AgentBuilder.WithSkillsRegistry"/>.
/// <para>
/// Build-time skills are already described in the static base prompt via
/// <c>WithSkillsIndex</c>. This contributor only surfaces skills enabled <em>after</em>
/// <see cref="AgentBuilder.Build"/> was called (e.g. by a channel plugin or tool call).
/// </para>
/// </summary>
public sealed class RuntimeSkillsContributor : IPromptContributor
{
    private readonly SkillRegistry _skillRegistry;
    private readonly HashSet<string> _buildTimeSkillNames;
    public string ContributorId => "runtime-skills";

    public RuntimeSkillsContributor(SkillRegistry skillRegistry, IEnumerable<string> buildTimeSkillNames)
    {
        _skillRegistry = skillRegistry;
        _buildTimeSkillNames = new HashSet<string>(buildTimeSkillNames, StringComparer.OrdinalIgnoreCase);
    }

    public string? GetFragment()
    {
        var runtimeSkills = _skillRegistry.GetEnabledSkills()
            .Where(s => !_buildTimeSkillNames.Contains(s.Name))
            .ToList();

        if (runtimeSkills.Count == 0) return null;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Skills Loaded at Runtime");
        foreach (var skill in runtimeSkills)
            sb.AppendLine($"- **{skill.Name}**: {skill.Description}");
        return sb.ToString().TrimEnd();
    }
}
