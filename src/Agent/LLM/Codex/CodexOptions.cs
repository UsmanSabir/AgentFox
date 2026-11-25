namespace AgentFox.LLM.Codex;

/// <summary>
/// Settings for the Codex provider (the owner's own ChatGPT subscription, through the official Codex CLI). Its own
/// <c>Codex</c> section, like <c>ClaudeCode</c>, so a hosted agent's AI settings file (which replaces <c>LLM</c>) cannot
/// wipe it. The owner's choices (model, reasoning effort) live in <see cref="CodexStore"/>.
/// </summary>
public sealed class CodexOptions
{
    public const string SectionName = "Codex";

    /// <summary>The CLI. A bare name is looked up on PATH.</summary>
    public string ExecutablePath { get; set; } = "codex";

    /// <summary>Where the CLI keeps its private CODEX_HOME (and the sign-in), home and working folders. Empty:
    /// <c>{workspace}/codex</c>. Never under the system temp folder: the CLI refuses to set up there.</summary>
    public string DataDirectory { get; set; } = "";

    /// <summary>Model used when the owner has not picked one. Empty: the CLI's own default for the account.</summary>
    public string DefaultModel { get; set; } = "";

    /// <summary>Choices offered in the UI besides the CLI's default. Not hardcoded: which models a ChatGPT plan offers
    /// changes, and any name the CLI accepts also works.</summary>
    public string[] Models { get; set; } = [];

    public int RequestTimeoutSeconds { get; set; } = 300;
    public int QueueTimeoutSeconds { get; set; } = 120;

    /// <summary>Calls at once. One by default: a call starts a process (measured 77-244 MB) and the plan's usage is
    /// shared with the owner's own use of ChatGPT and Codex.</summary>
    public int MaxConcurrentRequests { get; set; } = 1;

    /// <summary>How long a device sign-in waits for the owner to approve. OpenAI's code lasts 15 minutes.</summary>
    public int SignInTimeoutSeconds { get; set; } = 900;
}
