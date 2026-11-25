using System.Collections;
using AgentFox.LLM.Subscription;

namespace AgentFox.LLM.ClaudeCode;

/// <summary>
/// How the CLI is started: its arguments and its environment. Pure, so the two rules that matter are tested
/// without a process — no inherited credential can switch the call to paid API billing, and nothing of the owner's
/// own Claude setup loads.
/// </summary>
public static class ClaudeCodeCommand
{
    /// <summary>The child's whole environment.</summary>
    /// <param name="parent">This process's environment.</param>
    /// <param name="token">The setup token; null for a sign-in, which has none yet.</param>
    public static Dictionary<string, string> Environment(IDictionary parent, string? token, ClaudeCodePaths paths)
    {
        // Allow-listed inheritance and a private home: with the owner's own ~/.claude a call loads their skills, plugins
        // and auto-memory (measured 2026-11-25, even with --tools "" and --strict-mcp-config). Auth precedence puts
        // ANTHROPIC_AUTH_TOKEN, ANTHROPIC_API_KEY and apiKeyHelper ABOVE the subscription token, and in -p mode an API
        // key is always used when present, so none of them may be inherited.
        var env = CliEnvironment.Base(parent, paths.Home);
        env["CLAUDE_CONFIG_DIR"] = paths.Config;

        env["DISABLE_AUTOUPDATER"] = "1";            // the image pins the version; bare mode may become the default
        env["DISABLE_TELEMETRY"] = "1";
        env["DISABLE_ERROR_REPORTING"] = "1";
        env["CLAUDE_CODE_DISABLE_AUTO_MEMORY"] = "1";
        env["CLAUDE_CODE_DISABLE_CLAUDE_MDS"] = "1";
        env["CLAUDE_CODE_DISABLE_BUNDLED_SKILLS"] = "1";
        env["ENABLE_CLAUDEAI_MCP_SERVERS"] = "false";
        if (token is not null)
            env["CLAUDE_CODE_OAUTH_TOKEN"] = token;
        return env;
    }

    /// <summary>Arguments for one call. Output is always stream-json, so one parser serves both call shapes.</summary>
    /// <param name="systemPromptFile">A file holding the system prompt: Windows caps a command line at ~32K
    /// characters and the main agent's prompt can exceed it.</param>
    /// <param name="jsonSchema">Asks for validated JSON (read from the result's <c>structured_output</c>).</param>
    public static List<string> Arguments(string model, string systemPromptFile, string? jsonSchema)
    {
        var args = new List<string>
        {
            "-p",
            "--model", model,
            "--output-format", "stream-json",
            "--verbose",
            "--include-partial-messages",
            "--tools", "",                 // no built-in tool: no shell, no files, no web
            "--strict-mcp-config",         // no MCP server from any config
            "--no-session-persistence",    // each call stands alone; the host owns the conversation
            "--system-prompt-file", systemPromptFile,
        };
        if (jsonSchema is not null)
            args.AddRange(["--json-schema", jsonSchema]);
        // Never --dangerously-skip-permissions, never --bare: bare mode does not read the subscription token at all.
        return args;
    }
}

/// <summary>The CLI's private folders, under the data directory.</summary>
public sealed record ClaudeCodePaths(string Root)
{
    public string Home => Path.Combine(Root, "home");
    public string Config => Path.Combine(Root, "config");
    /// <summary>An empty working folder, so no project CLAUDE.md or settings are found.</summary>
    public string Work => Path.Combine(Root, "work");

    /// <summary>The CLI's leftovers, swept after a day (§0.1): even with session saving off it writes a backup of its
    /// <c>.claude.json</c> on each run plus <c>projects/</c> and <c>sessions/</c> entries (measured 2026-11-25), and a
    /// crashed call can leave its system-prompt file. The CLI's own small config files stay.</summary>
    public IEnumerable<string> SweptDirectories =>
        [Path.Combine(Config, "backups"), Path.Combine(Config, "projects"), Path.Combine(Config, "sessions"), Work];

    public void Ensure()
    {
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(Config);
        Directory.CreateDirectory(Work);
    }
}
