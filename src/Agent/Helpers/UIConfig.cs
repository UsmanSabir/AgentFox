namespace AgentFox.Helpers;


// ─────────────────────────────────────────────────────────────────────────────
// UI / Logging configuration models
// ─────────────────────────────────────────────────────────────────────────────

public class UIConfig
{
    /// <summary>When false, hides the reasoning panel and shows a spinner instead.</summary>
    public bool RenderReasoning { get; set; } = true;
}
