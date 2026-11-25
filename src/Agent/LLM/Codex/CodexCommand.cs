using System.Collections;
using AgentFox.LLM.Subscription;

namespace AgentFox.LLM.Codex;

/// <summary>
/// How the Codex CLI is started. Pure, so the isolation is tested without a process. Codex is a coding agent with a
/// shell; MEASURED 2026-11-25 (CLI 0.172.0, linux-x64): asked to read a canary file with its default tools it ran
/// <c>cat</c> on it, and only a sandbox that happened not to start in that container kept the contents back. With the
/// tool features below disabled it attempted nothing. Both layers stay: the tools are switched off, and the read-only
/// sandbox remains in case a future version adds a tool this list does not name.
/// </summary>
public static class CodexCommand
{
    /// <summary>Every feature that gives the model a way to act. Names from <c>codex features list</c> on the pinned
    /// version; a name a later version removes is rejected loudly by the CLI, which is the right failure.</summary>
    public static readonly string[] DisabledFeatures =
    [
        "shell_tool", "unified_exec", "plugins", "hooks", "apps", "browser_use", "computer_use", "view_image",
        "skill_search", "tool_suggest",
    ];

    public static Dictionary<string, string> Environment(IDictionary parent, CodexPaths paths)
    {
        // Allow-listed: OPENAI_API_KEY / CODEX_API_KEY would make a call bill the API instead of the plan.
        var env = CliEnvironment.Base(parent, paths.User);
        env["CODEX_HOME"] = paths.Home;
        env["NO_COLOR"] = "1";
        return env;
    }

    /// <param name="model">Null: the CLI's default for the account.</param>
    /// <param name="reasoningEffort">Null: the CLI's default.</param>
    /// <param name="schemaFile">A JSON Schema file for validated JSON output.</param>
    public static List<string> Arguments(string? model, string? reasoningEffort, string? schemaFile)
    {
        var args = new List<string>
        {
            "exec",
            "--json",                   // JSONL events on stdout
            "--ephemeral",              // no session files; the host owns the conversation
            "--ignore-user-config",     // not even the private CODEX_HOME's config.toml
            "--ignore-rules",
            "--skip-git-repo-check",    // the working folder is an empty private one
            "--sandbox", "read-only",
            "-c", "approval_policy=\"never\"",
        };
        foreach (var feature in DisabledFeatures)
            args.AddRange(["--disable", feature]);
        if (!string.IsNullOrWhiteSpace(model)) args.AddRange(["-m", model]);
        if (!string.IsNullOrWhiteSpace(reasoningEffort)) args.AddRange(["-c", $"model_reasoning_effort=\"{reasoningEffort}\""]);
        if (schemaFile is not null) args.AddRange(["--output-schema", schemaFile]);
        args.Add("-");                  // the prompt is read from stdin
        // Never --dangerously-bypass-approvals-and-sandbox, --full-auto or --search.
        return args;
    }
}

/// <summary>The CLI's private folders, under the data directory.</summary>
public sealed record CodexPaths(string Root)
{
    /// <summary>CODEX_HOME: the CLI's config and the sign-in (<c>auth.json</c>, CLI-managed).</summary>
    public string Home => Path.Combine(Root, "codex-home");
    /// <summary>HOME for the process.</summary>
    public string User => Path.Combine(Root, "home");
    /// <summary>An empty working folder, so no project instructions are found.</summary>
    public string Work => Path.Combine(Root, "work");
    public string AuthFile => Path.Combine(Home, "auth.json");

    /// <summary>The CLI's leftovers, swept after a day (§0.1). Never <see cref="Home"/> itself: the sign-in lives there.</summary>
    public IEnumerable<string> SweptDirectories => [Path.Combine(Home, "log"), Path.Combine(Home, "sessions"), Work];

    public void Ensure()
    {
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(User);
        Directory.CreateDirectory(Work);
    }
}
