using System.Collections;
using System.Diagnostics;
using System.Text.RegularExpressions;
using AgentFox.Plugins.Interfaces;
using Microsoft.Extensions.Configuration;

namespace AgentFox.Plugins.Security;

/// <summary>
/// Process-wide gate that keeps provider credentials out of everything a model, a tool, or a
/// chat user can observe. Credentials reach this process through two doors only — the
/// user-owned configuration file (<c>appsettings.user.json</c> and friends) and
/// key-shaped environment variables — so the guard closes both, in four independent layers
/// because any single layer can be bypassed by a tool nobody remembered to gate:
///
/// <list type="number">
/// <item><see cref="IsProtectedPath"/> — files that hold credentials (appsettings*.json,
///   <c>.env</c>, <c>*.plugin-config.json</c>, SSH/cloud/npm credential stores) are refused
///   for read, write, delete, and content search by the file tools.</item>
/// <item><see cref="TryDenyPayload"/> — shell commands and sandboxed code whose purpose is to
///   dump the environment (<c>set</c>, <c>printenv</c>, <c>$env:OPENAI_API_KEY</c>,
///   <c>os.environ</c>, …) or to cat a protected file are refused before a process starts.</item>
/// <item><see cref="SanitizeChildEnvironment"/> — child processes we launch (shell tool, code
///   sandbox) start with every secret-shaped variable stripped, so even a command we failed
///   to recognise has nothing to print.</item>
/// <item><see cref="Scrub"/> — every tool result is filtered for live secret values and
///   well-known credential shapes on the way back to the model. This is the backstop: when a
///   path nobody gated leaks a key, the model still never receives it.</item>
/// </list>
///
/// The guard is static because tools are constructed in a dozen places (registry, skills,
/// plugins, tests) with no shared container, and a gate that is only wired up sometimes is
/// not a gate. <see cref="Initialize"/> is called once from startup; until then the guard
/// still works off environment variables and shape patterns alone.
/// </summary>
public static class SecretGuard
{
    /// <summary>What a redacted secret is replaced with in tool output.</summary>
    public const string Placeholder = "[REDACTED:SECRET]";

    private static readonly object Sync = new();

    private static IConfiguration? _configuration;
    private static SecretGuardOptions _options = new();
    private static readonly HashSet<string> ManualSecrets = new(StringComparer.Ordinal);

    // Snapshot of live secret VALUES, longest-first so overlapping values redact fully.
    private static string[] _secretValues = [];
    private static DateTime _snapshotUtc = DateTime.MinValue;
    private static readonly TimeSpan SnapshotTtl = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Binds the guard to the composed configuration. Call once during startup, after the
    /// configuration stack is complete — the guard reads it to learn which values are live
    /// secrets, and reads the <c>Security</c> section for its own options.
    /// </summary>
    public static void Initialize(IConfiguration? configuration)
    {
        lock (Sync)
        {
            _configuration = configuration;
            _options = SecretGuardOptions.FromConfiguration(configuration);
            _snapshotUtc = DateTime.MinValue;
        }
    }

    /// <summary>
    /// Registers a secret discovered at runtime (a decrypted plugin secret, a token obtained
    /// from an OAuth exchange) so <see cref="Scrub"/> can redact it even though it never
    /// appears in configuration or the environment.
    /// </summary>
    public static void RegisterSecret(string? value)
    {
        if (!LooksLikeSecretValue(value)) return;
        lock (Sync)
        {
            ManualSecrets.Add(value!.Trim());
            _snapshotUtc = DateTime.MinValue;
        }
    }

    /// <summary>Guard options in effect (bound from the <c>Security</c> configuration section).</summary>
    public static SecretGuardOptions Options
    {
        get { lock (Sync) return _options; }
    }

    // ── Layer 1: protected paths ──────────────────────────────────────────────

    /// <summary>
    /// True when the path is a credential store that no tool may read, write, delete, or
    /// search. Matching is done on the resolved full path so <c>..</c> traversal and
    /// relative spellings cannot slip past.
    /// </summary>
    public static bool IsProtectedPath(string? path)
    {
        if (!Options.Enabled || string.IsNullOrWhiteSpace(path)) return false;

        string full;
        try { full = Path.GetFullPath(path); }
        catch { return true; }   // unparseable path: refuse rather than guess

        var fileName = Path.GetFileName(full);
        if (MatchesProtectedFileName(fileName)) return true;

        // Credential directories (~/.ssh, ~/.aws, ~/.kube, …) — any file underneath.
        foreach (var segment in full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (ProtectedDirectoryNames.Contains(segment)) return true;
        }

        foreach (var extra in Options.AdditionalProtectedPaths)
        {
            if (string.IsNullOrWhiteSpace(extra)) continue;
            if (fileName.Equals(extra, StringComparison.OrdinalIgnoreCase)) return true;
            try
            {
                if (full.Equals(Path.GetFullPath(extra), StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { /* not a path — treated as a file-name pattern above */ }
        }

        // The user-owned config file can be relocated via AGENTFOX_CONFIG_FILE.
        var configured = Environment.GetEnvironmentVariable("AGENTFOX_CONFIG_FILE");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                var resolved = Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));
                if (full.Equals(resolved, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { /* ignore an unparseable override */ }
        }

        return false;
    }

    /// <summary>Standard refusal message, so every tool answers the same way.</summary>
    public static string ProtectedPathMessage(string path) =>
        $"Access to '{path}' is denied: it holds credentials (API keys / secrets). " +
        "Configuration and secrets are managed by the operator through onboarding, the Doctor, " +
        "or the web configuration UI — they are never readable or writable through tools.";

    private static bool MatchesProtectedFileName(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return false;
        var name = fileName.ToLowerInvariant();

        if (ProtectedFileNames.Contains(name)) return true;
        if (name.StartsWith("appsettings", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal)) return true;
        if (name.EndsWith(".plugin-config.json", StringComparison.Ordinal)) return true;
        if (name.StartsWith(".env", StringComparison.Ordinal)) return true;
        if (name.StartsWith("id_rsa", StringComparison.Ordinal) ||
            name.StartsWith("id_dsa", StringComparison.Ordinal) ||
            name.StartsWith("id_ecdsa", StringComparison.Ordinal) ||
            name.StartsWith("id_ed25519", StringComparison.Ordinal)) return true;

        foreach (var extension in ProtectedExtensions)
        {
            if (name.EndsWith(extension, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static readonly HashSet<string> ProtectedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "secrets.json", "user-secrets.json", "credentials", "credentials.json",
        ".git-credentials", ".netrc", "_netrc", ".pgpass", ".npmrc", ".pypirc",
        ".dockercfg", "config.json.secret", "agentfox.secrets.json"
    };

    private static readonly HashSet<string> ProtectedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ssh", ".aws", ".azure", ".gnupg", ".kube", ".docker", ".gcloud", "microsoft.usersecrets"
    };

    private static readonly string[] ProtectedExtensions =
        [".pfx", ".p12", ".pem", ".key", ".jks", ".keystore", ".asc", ".gpg", ".ppk"];

    // ── Layer 2: shell / code payload denial ──────────────────────────────────

    /// <summary>
    /// Refuses a shell command or code payload whose evident purpose is to read credentials —
    /// dumping the environment, expanding a key-shaped variable, or opening a protected file.
    /// Returns true when the payload must NOT run, with <paramref name="reason"/> set.
    /// </summary>
    public static bool TryDenyPayload(string? payload, out string reason)
    {
        reason = string.Empty;
        if (!Options.Enabled || string.IsNullOrWhiteSpace(payload)) return false;

        foreach (var (pattern, why) in ProbePatterns)
        {
            if (IsMatch(pattern, payload))
            {
                reason =
                    $"Command refused by the secret guard: {why}. Credentials (API keys, tokens, " +
                    "passwords) are not readable by tools. Ask the operator to make the change instead.";
                return true;
            }
        }

        if (IsMatch(ProtectedFileMentionPattern, payload))
        {
            reason =
                "Command refused by the secret guard: it references a credential file " +
                "(appsettings/.env/plugin-config/SSH key). Those files are not readable through tools.";
            return true;
        }

        return false;
    }

    // Each entry: a payload shape that only makes sense when harvesting credentials.
    // Deliberately narrow — `$env:PATH`, `echo %CD%`, `set FOO=1`, `env FOO=1 cmd` stay legal.
    private static readonly (Regex Pattern, string Why)[] ProbePatterns =
    [
        // Bare `set` / `env` / `printenv` / `export`: prints every variable. An assignment
        // (`set FOO=1`) or a prefixed command (`env FOO=1 cmd`) has an argument and is fine.
        (new Regex(@"(?im)(?:^|[\s&|;(])(?:set|env|printenv|export)[ \t]*(?:$|[\r\n&|;>)])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)),
            "it dumps the whole process environment"),

        // `set OPENAI` / `printenv ANTHROPIC_API_KEY`: targeted variable printing.
        (new Regex(@"(?im)(?:^|[\s&|;(])(?:set|printenv|echo)[ \t]+[A-Za-z0-9_]*(?:key|token|secret|password|passwd|credential|openai|anthropic|api)[A-Za-z0-9_]*[ \t]*(?:$|[\r\n&|;>)])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)),
            "it prints a credential environment variable"),

        (new Regex(@"(?i)\b(?:get-childitem|gci|ls|dir|gc|cat|type|get-item)\s+(?:-path\s+)?env:",
            RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)),
            "it enumerates the PowerShell environment drive"),

        (new Regex(@"(?i)\[(?:System\.)?Environment\]::GetEnvironmentVariables?",
            RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)),
            "it reads environment variables directly"),

        (new Regex(@"(?i)\bEnvironment\.GetEnvironmentVariables?\s*\(",
            RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)),
            "it reads environment variables directly"),

        (new Regex(@"(?i)\b(?:os\.environ|os\.getenv|process\.env|getenv|ENV\[)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)),
            "it reads environment variables directly"),

        // $env:OPENAI_API_KEY  /  $env:anything_secret
        (new Regex(@"(?i)\$env:[A-Za-z0-9_]*(?:key|token|secret|password|passwd|credential)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)),
            "it expands a credential environment variable"),

        // %OPENAI_API_KEY%
        (new Regex(@"(?i)%[A-Za-z0-9_]*(?:KEY|TOKEN|SECRET|PASSWORD|PASSWD|CREDENTIAL)[A-Za-z0-9_]*%",
            RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)),
            "it expands a credential environment variable"),

        // $OPENAI_API_KEY / ${ANTHROPIC_API_KEY} — case-sensitive so `$token` in a script is fine.
        (new Regex(@"\$\{?[A-Z][A-Z0-9_]*(?:KEY|TOKEN|SECRET|PASSWORD|PASSWD|CREDENTIAL)[A-Z0-9_]*\}?",
            RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)),
            "it expands a credential environment variable")
    ];

    private static readonly Regex ProtectedFileMentionPattern = new(
        @"(?i)(?:appsettings[A-Za-z0-9._-]*\.json|[A-Za-z0-9._-]*\.plugin-config\.json|(?:^|[\s""'=\\/])\.env(?:\.[A-Za-z0-9_-]+)?\b|id_rsa|id_ed25519|\.git-credentials|\.netrc|\.pgpass|\.npmrc|secrets\.json|[A-Za-z0-9._-]+\.(?:pfx|p12|pem|jks|keystore|ppk)\b)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    // ── Layer 3: child-process environment ───────────────────────────────────

    /// <summary>
    /// Strips credential-shaped variables from a child process's environment before it starts.
    /// Requires <c>UseShellExecute = false</c> (the only mode we launch with). Set
    /// <c>Security:SanitizeChildProcessEnvironment=false</c> when a tool genuinely needs a
    /// token in its environment, and allowlist the specific variable instead.
    /// </summary>
    public static void SanitizeChildEnvironment(ProcessStartInfo startInfo)
    {
        var options = Options;
        if (!options.Enabled || !options.SanitizeChildProcessEnvironment) return;

        var secrets = SecretValues();
        var doomed = new List<string>();

        foreach (var key in startInfo.Environment.Keys)
        {
            if (options.ChildProcessEnvironmentAllowlist.Contains(key)) continue;

            var value = startInfo.Environment[key];
            var isSecret = IsSecretName(key) ||
                           (value is not null && secrets.Contains(value.Trim(), StringComparer.Ordinal));
            if (isSecret) doomed.Add(key);
        }

        foreach (var key in doomed)
            startInfo.Environment.Remove(key);
    }

    // ── Layer 4: output scrubbing ─────────────────────────────────────────────

    /// <summary>
    /// Removes live secret values and well-known credential shapes from text on its way back
    /// to a model or a user. Idempotent, so applying it at several layers is harmless.
    /// </summary>
    public static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        if (!Options.Enabled) return text;

        var result = text;

        // Exact live values first — the only match that is certain.
        foreach (var secret in SecretValues())
        {
            if (result.Contains(secret, StringComparison.Ordinal))
                result = result.Replace(secret, Placeholder, StringComparison.Ordinal);
        }

        // Provider-specific credential shapes, live or not.
        foreach (var pattern in ShapePatterns)
            result = SafeReplace(pattern, result, _ => Placeholder);

        // NAME=value / "NAME": "value" where NAME reads as a credential.
        result = SafeReplace(AssignmentPattern, result, match =>
            IsSecretName(match.Groups["name"].Value)
                ? match.Groups["name"].Value + match.Groups["sep"].Value + Placeholder
                : match.Value);

        return result;
    }

    /// <summary>
    /// Scrubs a tool result's output, error, and metadata in place. Called from
    /// <c>BaseTool.ExecuteAsync</c> and again from the agent's tool gateway, so results from
    /// tools that implement <c>ITool</c> directly (MCP bridges, Composio wrappers) are covered too.
    /// </summary>
    public static ToolResult ScrubInPlace(ToolResult? result)
    {
        if (result is null) return ToolResult.Fail("No result");
        if (!Options.Enabled) return result;

        result.Output = Scrub(result.Output);
        if (result.Error is not null)
            result.Error = Scrub(result.Error);

        if (result.Metadata.Count > 0)
        {
            foreach (var key in result.Metadata.Keys.ToList())
            {
                result.Metadata[key] = IsSecretName(key)
                    ? Placeholder
                    : Scrub(result.Metadata[key]);
            }
        }

        return result;
    }

    /// <summary>
    /// Redacts only the exact live secret values, leaving credential-shaped text alone. Used on
    /// assistant messages: the model must not repeat the operator's real key (it may have been
    /// recorded in a transcript before this guard existed), but it must still be able to write
    /// documentation like <c>OPENAI_API_KEY=your-key-here</c> without it turning into noise.
    /// </summary>
    public static string ScrubKnownValues(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        if (!Options.Enabled) return text;

        var result = text;
        foreach (var secret in SecretValues())
        {
            if (result.Contains(secret, StringComparison.Ordinal))
                result = result.Replace(secret, Placeholder, StringComparison.Ordinal);
        }
        return result;
    }

    /// <summary>True when the text carries a live secret value — used to block outbound exfiltration.</summary>
    public static bool ContainsSecret(string? text)
    {
        if (!Options.Enabled || string.IsNullOrEmpty(text)) return false;
        foreach (var secret in SecretValues())
        {
            if (text.Contains(secret, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static readonly Regex[] ShapePatterns =
    [
        Shape(@"sk-ant-[A-Za-z0-9\-_]{20,}"),                    // Anthropic
        Shape(@"sk-(?:proj-|or-v1-|live-)?[A-Za-z0-9\-_]{20,}"), // OpenAI / OpenRouter
        Shape(@"tvly-[A-Za-z0-9\-_]{10,}"),                      // Tavily
        Shape(@"BSA[A-Za-z0-9\-_]{20,}"),                        // Brave Search
        Shape(@"AIza[0-9A-Za-z\-_]{30,}"),                       // Google
        Shape(@"gh[pousr]_[A-Za-z0-9]{20,}"),                     // GitHub
        Shape(@"github_pat_[A-Za-z0-9_]{20,}"),                   // GitHub fine-grained
        Shape(@"xox[abposr]-[A-Za-z0-9\-]{10,}"),                 // Slack
        Shape(@"(?i)\bbearer\s+[A-Za-z0-9\-._~+/]{20,}={0,2}"),   // Authorization headers
        Shape(@"\bey[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}")  // JWT
    ];

    private static Regex Shape(string pattern) =>
        new(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    // Value must be 6+ chars with no whitespace so `token: missing` or `max_tokens=64` survive.
    private static readonly Regex AssignmentPattern = new(
        @"(?i)(?<name>[A-Za-z0-9_.\-]*(?:api[_-]?key|secret|token|password|passwd|credential|private[_-]?key|authorization)[A-Za-z0-9_.\-]*)(?<sep>\s*[""']?\s*[:=]\s*[""']?)(?<value>[^\s""',;)}\]]{6,})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    private static string SafeReplace(Regex pattern, string input, MatchEvaluator evaluator)
    {
        try { return pattern.Replace(input, evaluator); }
        catch (RegexMatchTimeoutException) { return input; }
    }

    private static bool IsMatch(Regex pattern, string input)
    {
        try { return pattern.IsMatch(input); }
        catch (RegexMatchTimeoutException) { return true; }   // pathological input: refuse
    }

    // ── Secret name / value recognition ───────────────────────────────────────

    /// <summary>
    /// True when a configuration key or environment variable name reads as a credential.
    /// The exclusion list matters as much as the inclusion list: <c>MaxTokens</c> contains
    /// "token" but its value ("4096") must never be treated as a secret to scrub.
    /// </summary>
    public static bool IsSecretName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var normalized = Normalize(name);

        foreach (var fragment in NonSecretNameFragments)
        {
            if (normalized.Contains(fragment, StringComparison.Ordinal)) return false;
        }
        foreach (var fragment in SecretNameFragments)
        {
            if (normalized.Contains(fragment, StringComparison.Ordinal)) return true;
        }
        foreach (var extra in Options.AdditionalSecretNames)
        {
            if (!string.IsNullOrWhiteSpace(extra) &&
                normalized.Contains(Normalize(extra), StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static string Normalize(string value) =>
        value.Replace("-", string.Empty).Replace("_", string.Empty)
             .Replace(" ", string.Empty).Replace(":", string.Empty)
             .ToLowerInvariant();

    private static readonly string[] SecretNameFragments =
    [
        "apikey", "apisecret", "accesskey", "secretkey", "privatekey", "publickey",
        "token", "secret", "password", "passwd", "credential", "authorization",
        "connectionstring", "clientsecret", "webhooksecret", "signingkey",
        "subscriptiontoken", "tradingpin", "sessionkey", "salt", "passphrase"
    ];

    // Names that merely contain a secret-ish word but hold ordinary data.
    private static readonly string[] NonSecretNameFragments =
    [
        "maxtoken", "mintoken", "tokenlimit", "tokenbudget", "tokencount", "tokencounter",
        "tokenizer", "tokenusage", "tokensused", "tokenspersecond", "inputtoken",
        "outputtoken", "prompttoken", "completiontoken", "cachedtoken", "totaltoken",
        "estimatedtoken", "tokenthreshold", "tokenpath", "publickeypath",
        "passwordpolicy", "secretname", "apikeyname", "apikeyheader", "apikeyenvvar",
        "tokenenvvar", "hastoken", "hasapikey", "hassecret", "hascredential",
        "tokensavailable", "keyvaultname", "secretsource", "requiresapikey"
    ];

    /// <summary>
    /// True when a value is plausible as a real credential. The length and shape floors keep
    /// placeholders, booleans, and small numbers out of the scrub list — scrubbing "4096"
    /// or "true" from every tool result would quietly corrupt output everywhere.
    /// </summary>
    public static bool LooksLikeSecretValue(string? value)
    {
        var candidate = value?.Trim();
        if (string.IsNullOrEmpty(candidate)) return false;
        if (candidate.Length < 12 || candidate.Length > 4096) return false;
        if (candidate.Any(char.IsWhiteSpace)) return false;
        if (candidate.All(char.IsDigit)) return false;
        if (!candidate.Any(char.IsLetterOrDigit)) return false;

        foreach (var marker in PlaceholderMarkers)
        {
            if (candidate.Contains(marker, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static readonly string[] PlaceholderMarkers =
    [
        "your-", "your_", "yourkey", "changeme", "change-me", "replace-me", "replaceme",
        "placeholder", "example.com", "xxxx", "<", ">", "${", "todo", "dummy", "sample",
        "notset", "not-set", "n/a"
    ];

    // ── Live secret snapshot ──────────────────────────────────────────────────

    private static string[] SecretValues()
    {
        lock (Sync)
        {
            if (DateTime.UtcNow - _snapshotUtc < SnapshotTtl)
                return _secretValues;

            var values = new HashSet<string>(StringComparer.Ordinal);

            try
            {
                foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
                {
                    var name = entry.Key?.ToString();
                    var value = entry.Value?.ToString();
                    if (name is not null && IsSecretName(name) && LooksLikeSecretValue(value))
                        values.Add(value!.Trim());
                }
            }
            catch { /* environment unavailable — shape patterns still apply */ }

            if (_configuration is not null)
            {
                try
                {
                    foreach (var pair in _configuration.AsEnumerable())
                    {
                        if (pair.Value is null) continue;
                        if (IsSecretName(pair.Key) && LooksLikeSecretValue(pair.Value))
                            values.Add(pair.Value.Trim());
                    }
                }
                catch { /* a provider threw — never let the guard break startup */ }
            }

            foreach (var manual in ManualSecrets)
                values.Add(manual);

            // Longest first: a key that contains another as a substring still redacts whole.
            _secretValues = values.OrderByDescending(v => v.Length).ToArray();
            _snapshotUtc = DateTime.UtcNow;
            return _secretValues;
        }
    }

    /// <summary>Test seam: forget the cached snapshot and any registered runtime secrets.</summary>
    public static void ResetForTests()
    {
        lock (Sync)
        {
            _configuration = null;
            _options = new SecretGuardOptions();
            ManualSecrets.Clear();
            _secretValues = [];
            _snapshotUtc = DateTime.MinValue;
        }
    }
}

/// <summary>
/// Options for <see cref="SecretGuard"/>, bound from the <c>Security</c> configuration section.
/// </summary>
public sealed class SecretGuardOptions
{
    public const string SectionName = "Security";

    /// <summary>
    /// Master switch. Disabling it turns off path denial, payload denial, environment
    /// sanitising, and output scrubbing — API keys then flow freely into model context.
    /// Only set false in an isolated environment with throwaway credentials.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Strip credential-shaped variables from processes the agent launches.</summary>
    public bool SanitizeChildProcessEnvironment { get; init; } = true;

    /// <summary>
    /// Environment variables to keep in child processes despite their name (e.g. a
    /// <c>GH_TOKEN</c> a git tool genuinely needs). Each entry re-opens a leak path.
    /// </summary>
    public HashSet<string> ChildProcessEnvironmentAllowlist { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Extra file names or absolute paths to treat as credential stores.</summary>
    public string[] AdditionalProtectedPaths { get; init; } = [];

    /// <summary>Extra configuration/environment name fragments that identify a secret.</summary>
    public string[] AdditionalSecretNames { get; init; } = [];

    internal static SecretGuardOptions FromConfiguration(IConfiguration? configuration)
    {
        var section = configuration?.GetSection(SectionName);
        if (section is null || !section.Exists()) return new SecretGuardOptions();

        var allowlist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in section.GetSection(nameof(ChildProcessEnvironmentAllowlist)).Get<string[]>() ?? [])
        {
            if (!string.IsNullOrWhiteSpace(entry)) allowlist.Add(entry.Trim());
        }

        return new SecretGuardOptions
        {
            Enabled = section.GetValue(nameof(Enabled), true),
            SanitizeChildProcessEnvironment = section.GetValue(nameof(SanitizeChildProcessEnvironment), true),
            ChildProcessEnvironmentAllowlist = allowlist,
            AdditionalProtectedPaths = section.GetSection(nameof(AdditionalProtectedPaths)).Get<string[]>() ?? [],
            AdditionalSecretNames = section.GetSection(nameof(AdditionalSecretNames)).Get<string[]>() ?? []
        };
    }
}
