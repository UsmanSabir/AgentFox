namespace AgentFox.Hitl;

/// <summary>
/// Bound from the "Hitl" section of appsettings.json.
/// </summary>
public class HitlConfig
{
    /// <summary>Master switch — set to true to enable HITL approval flows.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Tool names that require human approval before execution.
    /// Example: ["shell", "delete", "write_file"]
    /// </summary>
    public List<string> RequireApprovalForTools { get; set; } = [];
}
