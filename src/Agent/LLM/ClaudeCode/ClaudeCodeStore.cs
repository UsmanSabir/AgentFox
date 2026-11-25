using System.Text.Json;
using AgentFox.Plugins;
using Microsoft.Extensions.Logging;

using AgentFox.LLM.Subscription;

namespace AgentFox.LLM.ClaudeCode;

/// <summary>What the owner chose for Claude Code. Which purposes use it lives in <see cref="AgentFox.LLM.Subscription.AiRoutingStore"/>.</summary>
/// <param name="TokenSavedUtc">When the token was saved; the estimated expiry counts from here.</param>
/// <param name="TokenSource"><c>signin</c> or <c>paste</c>.</param>
public sealed record ClaudeCodeSettings(bool HasToken, DateTimeOffset? TokenSavedUtc, string? TokenSource, string? Model);

/// <summary>
/// The token and the owner's choices, in <c>plugin-configs/claude-code.json</c>. The token is encrypted with the
/// plugin-secrets key (on a hosted agent that key lives outside the user's volume) and never leaves this class
/// except into the CLI's own environment.
///
/// Retention (§0.1): one file, replaced whole on each change, no age limit — it is configuration. "Remove" deletes
/// the token; deleting the account's data deletes the file.
/// </summary>
public sealed class ClaudeCodeStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path;
    private readonly IPluginSecretProtector _protector;
    private readonly ILogger<ClaudeCodeStore> _logger;
    private readonly object _gate = new();
    private Persisted? _cache;

    public ClaudeCodeStore(string path, IPluginSecretProtector protector, ILogger<ClaudeCodeStore> logger)
    {
        _path = path;
        _protector = protector;
        _logger = logger;
    }

    /// <summary>Set when the file could not be read or its token could not be decrypted (e.g. the key changed).</summary>
    public string? LoadProblem { get; private set; }

    public ClaudeCodeSettings Read()
    {
        lock (_gate)
        {
            var p = Load();
            return new ClaudeCodeSettings(
                HasToken: !string.IsNullOrEmpty(p.Token) && TryToken(p) is not null,
                p.TokenSavedUtc, p.TokenSource, p.Model);
        }
    }

    /// <summary>The decrypted token, for the CLI's environment only.</summary>
    public string? ReadToken()
    {
        lock (_gate) return TryToken(Load());
    }

    public void SaveToken(string token, string source)
    {
        if (!ClaudeCodeTerminalText.IsWellFormedToken(token))
            throw new ArgumentException("Not a Claude Code setup token.", nameof(token));
        lock (_gate)
        {
            var p = Load() with { Token = _protector.Protect(token), TokenSavedUtc = DateTimeOffset.UtcNow, TokenSource = source };
            Write(p);
        }
    }

    public void RemoveToken()
    {
        lock (_gate) Write(Load() with { Token = null, TokenSavedUtc = null, TokenSource = null });
    }

    public void SaveModel(string? model)
    {
        lock (_gate) Write(Load() with { Model = string.IsNullOrWhiteSpace(model) ? null : model.Trim() });
    }

    /// <summary>A per-purpose switch saved before routing moved to <see cref="AgentFox.LLM.Subscription.AiRoutingStore"/>;
    /// null when none was. Read once, as that store's starting point.</summary>
    public bool? LegacyUseFor(string purpose)
    {
        lock (_gate)
        {
            var p = Load();
            return purpose switch
            {
                AiPurposes.Chat => p.UseForChat,
                AiPurposes.StockAssessment => p.UseForStockAssessment,
                AiPurposes.RebuyAdvice => p.UseForRebuyAdvice,
                _ => null,
            };
        }
    }

    private string? TryToken(Persisted p)
    {
        if (string.IsNullOrEmpty(p.Token)) return null;
        if (_protector.TryUnprotect(p.Token, out var plain) && ClaudeCodeTerminalText.IsWellFormedToken(plain))
            return plain;
        LoadProblem = "The saved Claude token could not be read (the encryption key may have changed). Sign in again.";
        return null;
    }

    private Persisted Load()
    {
        if (_cache is not null) return _cache;
        if (!File.Exists(_path)) return _cache = new Persisted();
        try
        {
            _cache = JsonSerializer.Deserialize<Persisted>(File.ReadAllText(_path), Json) ?? new Persisted();
            LoadProblem = null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Unreadable is reported, not treated as configured. The next save from the UI replaces the file: it
            // holds only what the owner can re-enter, so nothing is lost that cannot be put back.
            _logger.LogWarning("[ClaudeCode] {Path} could not be read ({Type}).", _path, ex.GetType().Name);
            LoadProblem = "The Claude Code settings file could not be read. Saving again replaces it.";
            _cache = new Persisted();
        }
        return _cache;
    }

    private void Write(Persisted p)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(p, Json));
        File.Move(temp, _path, overwrite: true);
        _cache = p;
        LoadProblem = null;
    }

    private sealed record Persisted
    {
        public string? Token { get; init; }
        public DateTimeOffset? TokenSavedUtc { get; init; }
        public string? TokenSource { get; init; }
        public string? Model { get; init; }
        public bool? UseForChat { get; init; }
        public bool? UseForStockAssessment { get; init; }
        public bool? UseForRebuyAdvice { get; init; }
    }
}
