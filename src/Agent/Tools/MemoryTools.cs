using AgentFox.Models;
using AgentFox.Memory;

namespace AgentFox.Tools;

/// <summary>
/// Tool for explicitly adding new memories
/// </summary>
public class AddMemoryTool : BaseTool
{
    private readonly IMemory _memory;

    public override string Name => "add_memory";
    public override string Description => "Save an important fact, piece of information, or user preference to long-term memory for later recall.";
    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["content"] = new() { Type = "string", Description = "The information to remember", Required = true },
        ["importance"] = new() { Type = "number", Description = "Importance of this memory from 0.0 to 1.0 (default 0.8)", Required = false, Default = 0.8 }
    };

    public AddMemoryTool(IMemory memory)
    {
        _memory = memory;
    }

    protected override async Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var content = arguments["content"]?.ToString();
        if (string.IsNullOrEmpty(content))
            return ToolResult.Fail("No content provided to remember");

        var importance = 0.8;
        if (arguments.TryGetValue("importance", out var impObj) && impObj != null)
        {
            if (double.TryParse(impObj.ToString(), out var parsedImp))
                importance = Math.Clamp(parsedImp, 0.0, 1.0);
        }

        try
        {
            var entry = new MemoryEntry
            {
                Content = content,
                Type = MemoryType.Fact,
                Importance = importance,
                Timestamp = DateTime.UtcNow
            };

            await _memory.AddAsync(entry);
            return ToolResult.Ok($"Successfully saved to memory: {content}");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Failed to save memory: {ex.Message}");
        }
    }
}

/// <summary>
/// Tool that returns every entry stored in long-term memory.
/// Useful when the agent needs a full picture of what has been remembered.
/// </summary>
public class GetAllMemoriesTool : BaseTool
{
    private readonly IMemory _memory;

    public override string Name => "get_all_memories";
    public override string Description => "Retrieve every entry stored in long-term memory. Use this to get a complete picture of all remembered facts, preferences, and observations.";
    public override Dictionary<string, ToolParameter> Parameters { get; } = new();

    public GetAllMemoriesTool(IMemory memory)
    {
        _memory = memory;
    }

    protected override async Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        try
        {
            var memories = await _memory.GetAllAsync();
            if (memories.Count == 0)
                return ToolResult.Ok("No memories stored yet.");

            var lines = memories.Select((m, i) =>
                $"{i + 1}. [{m.Type}] [{m.Timestamp:yyyy-MM-dd HH:mm}] (imp:{m.Importance:F1}): {m.Content}");
            return ToolResult.Ok($"{memories.Count} stored memories:\n{string.Join("\n", lines)}");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Failed to retrieve memories: {ex.Message}");
        }
    }
}

/// <summary>
/// Tool for explicitly searching memory
/// </summary>
public class SearchMemoryTool : BaseTool
{
    private readonly IMemory _memory;

    public override string Name => "search_memory";
    public override string Description => "Search the agent's long-term and short-term memory for previously remembered facts or context.";
    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["query"] = new() { Type = "string", Description = "The topic or keyword to search for in memory", Required = true },
        ["limit"] = new() { Type = "number", Description = "Maximum number of results to return", Required = false, Default = 5 }
    };

    public SearchMemoryTool(IMemory memory)
    {
        _memory = memory;
    }

    protected override async Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var query = arguments["query"]?.ToString();
        if (string.IsNullOrEmpty(query))
            return ToolResult.Fail("No search query provided");

        var limit = 5;
        if (arguments.TryGetValue("limit", out var limObj) && limObj != null)
        {
            if (int.TryParse(limObj.ToString(), out var parsedLim))
                limit = Math.Max(1, parsedLim);
        }

        try
        {
            var results = await _memory.SearchAsync(query, limit);
            if (results.Count == 0)
            {
                return ToolResult.Ok($"No memories found matching '{query}'.");
            }

            var formatted = string.Join("\n", results.Select(m => $"- [{m.Timestamp:yyyy-MM-dd HH:mm}] (Importance: {m.Importance}): {m.Content}"));
            return ToolResult.Ok($"Found {results.Count} memories matching '{query}':\n{formatted}");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Failed to search memory: {ex.Message}");
        }
    }
}
