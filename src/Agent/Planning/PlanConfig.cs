namespace AgentFox.Planning;

/// <summary>
/// Bound from the "Plan" section of appsettings.json. Controls the structured
/// research → plan → execute workflow with a human approval gate between plan and execute.
/// </summary>
public class PlanConfig
{
    /// <summary>Master switch — when false, no plan gating or <c>submit_plan</c> tool is added.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Side-effecting tool names that are blocked until a plan has been approved for the session.
    /// Read-only tools (search, fetch, read_file …) are intentionally NOT listed so research
    /// can proceed freely. Example: ["place_order", "write_file", "shell", "delete"].
    /// </summary>
    public List<string> MutatingTools { get; set; } = [];
}
