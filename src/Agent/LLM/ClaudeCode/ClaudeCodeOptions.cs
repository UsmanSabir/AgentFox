namespace AgentFox.LLM.ClaudeCode;

/// <summary>
/// Settings for the Claude Code provider (the owner's own Claude subscription, through the official CLI).
/// Kept in its own <c>ClaudeCode</c> section, NOT under <c>LLM</c>: a hosted agent's AI settings file replaces the
/// whole <c>LLM</c> section, and would silently wipe these. What the owner chooses in the UI (model, which purposes
/// use it, the token) lives in <see cref="ClaudeCodeStore"/>, not here.
/// </summary>
public sealed class ClaudeCodeOptions
{
    public const string SectionName = "ClaudeCode";

    /// <summary>The CLI. A bare name is looked up on PATH.</summary>
    public string ExecutablePath { get; set; } = "claude";

    /// <summary>Where the CLI keeps its private config and working folders. Empty: <c>{workspace}/claude-code</c>.
    /// Deliberately NOT <c>~/.claude</c>: the owner's own skills, plugins and memory would load into every call.</summary>
    public string DataDirectory { get; set; } = "";

    /// <summary>Model used when the owner has not picked one.</summary>
    public string DefaultModel { get; set; } = "sonnet";

    /// <summary>Choices offered in the UI. Any name the CLI accepts also works.</summary>
    public string[] Models { get; set; } = ["sonnet", "opus", "haiku"];

    public int RequestTimeoutSeconds { get; set; } = 300;

    /// <summary>How long a call waits for the one before it to finish.</summary>
    public int QueueTimeoutSeconds { get; set; } = 120;

    /// <summary>Calls at once. One by default: each call starts a process of roughly 200-235 MB (measured
    /// 2026-11-25), and the subscription's usage is shared with the owner's own use of Claude.</summary>
    public int MaxConcurrentRequests { get; set; } = 1;

    /// <summary>How long a "Sign in with Claude" attempt waits for the code.</summary>
    public int SignInTimeoutSeconds { get; set; } = 600;

    /// <summary>A setup token's documented lifetime. Expiry shown in the UI is this many days after it was saved,
    /// labelled as an estimate.</summary>
    public int TokenLifetimeDays { get; set; } = 365;

    public int TokenExpiryWarningDays { get; set; } = 30;
}
