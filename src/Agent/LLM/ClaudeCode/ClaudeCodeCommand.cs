using System.Collections;

namespace AgentFox.LLM.ClaudeCode;

/// <summary>
/// How the CLI is started: its arguments and its environment. Pure, so the two rules that matter are tested
/// without a process — no inherited credential can switch the call to paid API billing, and nothing of the owner's
/// own Claude setup loads.
/// </summary>
public static class ClaudeCodeCommand
{
    /// <summary>
    /// Variables a child may inherit. An allow-list, not a strip-list: auth precedence puts
    /// <c>ANTHROPIC_AUTH_TOKEN</c>, <c>ANTHROPIC_API_KEY</c> and <c>apiKeyHelper</c> ABOVE the subscription token,
    /// and in <c>-p</c> mode an API key is always used when present. A strip-list misses the next one invented.
    /// </summary>
    private static readonly string[] Inherited =
    [
        "PATH", "PATHEXT", "SystemRoot", "SystemDrive", "windir", "ComSpec", "OS", "PROCESSOR_ARCHITECTURE",
        "NUMBER_OF_PROCESSORS", "TEMP", "TMP", "TMPDIR", "LANG", "LANGUAGE", "LC_ALL", "LC_CTYPE", "TZ", "TERM",
        "HTTPS_PROXY", "HTTP_PROXY", "NO_PROXY", "https_proxy", "http_proxy", "no_proxy",
        "SSL_CERT_FILE", "SSL_CERT_DIR", "NODE_EXTRA_CA_CERTS",
    ];

    /// <summary>The child's whole environment.</summary>
    /// <param name="parent">This process's environment.</param>
    /// <param name="token">The setup token; null for a sign-in, which has none yet.</param>
    public static Dictionary<string, string> Environment(IDictionary parent, string? token, ClaudeCodePaths paths)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in Inherited)
            if (parent[name] is string value && value.Length > 0)
                env[name] = value;

        // A private home and config folder. With the owner's own ~/.claude the call loads their skills, plugins
        // and auto-memory (measured 2026-11-25, even with --tools "" and --strict-mcp-config).
        env["HOME"] = paths.Home;
        env["USERPROFILE"] = paths.Home;
        env["APPDATA"] = Path.Combine(paths.Home, "AppData", "Roaming");
        env["LOCALAPPDATA"] = Path.Combine(paths.Home, "AppData", "Local");
        env["XDG_CONFIG_HOME"] = Path.Combine(paths.Home, ".config");
        env["XDG_CACHE_HOME"] = Path.Combine(paths.Home, ".cache");
        env["CLAUDE_CONFIG_DIR"] = paths.Config;

        env["DISABLE_AUTOUPDATER"] = "1";            // the image pins the version; bare mode may become the default
        env["DISABLE_TELEMETRY"] = "1";
        env["DISABLE_ERROR_REPORTING"] = "1";
        env["CLAUDE_CODE_DISABLE_AUTO_MEMORY"] = "1";
        env["CLAUDE_CODE_DISABLE_CLAUDE_MDS"] = "1";
        env["CLAUDE_CODE_DISABLE_BUNDLED_SKILLS"] = "1";
        env["ENABLE_CLAUDEAI_MCP_SERVERS"] = "false";
        env["BROWSER"] = "none";                     // a sign-in relays the link; nothing opens on the server
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

    public void Ensure()
    {
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(Config);
        Directory.CreateDirectory(Work);
    }
}
