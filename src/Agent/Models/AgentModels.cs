namespace AgentFox.Models;

using AgentFox.Memory;
using AgentFox.Skills;
using System.Diagnostics;

/// <summary>
/// Represents the role of a message in a conversation
/// </summary>
public enum MessageRole
{
    System,
    User,
    Assistant,
    Tool
}

/// <summary>
/// Represents a message in the agent's conversation
/// </summary>
public class Message
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public MessageRole Role { get; set; }
    public string Content { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string? ToolCallId { get; set; }
    public string? ToolName { get; set; }
    
    public Message() { }
    
    public Message(MessageRole role, string content)
    {
        Role = role;
        Content = content;
    }
}

/// <summary>
/// Represents a tool that can be called by the agent
/// </summary>
[DebuggerDisplay("{Name}")]
public class ToolDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public Dictionary<string, ToolParameter> Parameters { get; set; } = new();
}

/// <summary>
/// Represents a parameter for a tool
/// </summary>
public class ToolParameter
{
    public string Type { get; set; } = "string";
    public string Description { get; set; } = string.Empty;
    public bool Required { get; set; }
    public object? Default { get; set; }
    
    // Enhanced schema support
    public string? JsonSchema { get; set; }        // Full JSON Schema (optional)
    public object? Example { get; set; }           // Example value for documentation
    public string? Pattern { get; set; }           // Regex pattern (for string validation)
    public int? MinLength { get; set; }            // Min string length
    public int? MaxLength { get; set; }            // Max string length
    public decimal? Minimum { get; set; }          // Min numeric value
    public decimal? Maximum { get; set; }          // Max numeric value
}

/// <summary>
/// Represents a tool call made by the agent
/// </summary>
public class ToolCall
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ToolName { get; set; } = string.Empty;
    public Dictionary<string, object?> Arguments { get; set; } = new();
    public string? Result { get; set; }
    public bool IsCompleted { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Configuration for an agent
/// </summary>
public class AgentConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? SystemPrompt { get; set; }
    public string? Model { get; set; }
    public int MaxTokens { get; set; } = 4096;
    public double Temperature { get; set; } = 0.7;
    public List<ToolDefinition> Tools { get; set; } = new();
    public SkillRegistry? SkillRegistry { get; set; }
    public int MaxIterations { get; set; } = 10;
    public Dictionary<string, string> Metadata { get; set; } = new();
}

/// <summary>
/// Represents an agent with its state and configuration
/// </summary>
public class Agent
{
    const string GlobalMainSessionDefaultKey = "Main";
    public AgentConfig Config { get; set; } = new();
    public List<Message> ConversationHistory { get; set; } = new();
    public IConversationStore ConversationStore { get; set; }
    public string DefaultConversationId { get; set; } = GlobalMainSessionDefaultKey; //Guid.NewGuid().ToString("N");
    public IMemory? Memory { get; set; }
    public Agent? Parent { get; set; }
    public List<Agent> SubAgents { get; set; } = new();
    public AgentStatus Status { get; set; } = AgentStatus.Idle;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastActiveAt { get; set; }
    
    /// <summary>
    /// Skills enabled for this agent (used for tool lookup and execution)
    /// </summary>
    public List<Skill> EnabledSkills { get; set; } = new();
}

/// <summary>
/// Status of an agent
/// </summary>
public enum AgentStatus
{
    Idle,
    Thinking,
    ExecutingTool,
    WaitingForSubAgent,
    Completed,
    Error
}

/// <summary>
/// Result of an agent's execution
/// </summary>
public class AgentResult
{
    public bool Success { get; set; }
    public string Output { get; set; } = string.Empty;
    public List<ToolCall> ToolCalls { get; set; } = new();
    public List<Agent> SpawnedSubAgents { get; set; } = new();
    public string? Error { get; set; }
    public int Iterations { get; set; }
    public TimeSpan Duration { get; set; }
}
