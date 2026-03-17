namespace AgentFox.Agents;

/// <summary>
/// Represents different execution lanes for commands
/// This allows prioritization and segregation of different types of tasks
/// inspired by OpenClaw's approach to managing concurrent execution
/// </summary>
public enum CommandLane
{
    /// <summary>
    /// Main agent execution lane - highest priority, typically 1 task at a time
    /// </summary>
    Main,
    
    /// <summary>
    /// Sub-agent execution lane - dedicated lane for spawned sub-agents to run concurrently
    /// without blocking the main agent
    /// </summary>
    Subagent,
    
    /// <summary>
    /// Background tasks lane - for non-urgent operations like logging, cleanup, etc.
    /// </summary>
    Background,
    
    /// <summary>
    /// Tool execution lane - for long-running tool calls
    /// </summary>
    Tool
}
