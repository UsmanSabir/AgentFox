using System.Text.Json;
using AgentFox.LLM.Subscription;
using Microsoft.Extensions.Logging;

namespace AgentFox.LLM.Codex;

public sealed record CodexSettings(string? Model, string? ReasoningEffort);

/// <summary>
/// The owner's Codex choices in <c>plugin-configs/codex.json</c>. Holds NO credential: the sign-in is the CLI's own
/// <c>auth.json</c> in its private CODEX_HOME, which the CLI refreshes itself and which this agent never parses.
///
/// Retention (§0.1): one file, replaced whole on each change, no age limit — it is configuration. The CLI's
/// <c>auth.json</c> is removed by Sign out (<c>codex logout</c>) or with the account's data.
/// </summary>
public sealed class CodexStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path;
    private readonly ILogger<CodexStore> _logger;
    private readonly object _gate = new();
    private CodexSettings? _cache;

    public CodexStore(string path, ILogger<CodexStore> logger)
    {
        _path = path;
        _logger = logger;
    }

    public CodexSettings Read()
    {
        lock (_gate)
        {
            if (_cache is not null) return _cache;
            if (!File.Exists(_path)) return _cache = new CodexSettings(null, null);
            try
            {
                _cache = JsonSerializer.Deserialize<CodexSettings>(File.ReadAllText(_path), Json) ?? new CodexSettings(null, null);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("[Codex] {Path} could not be read ({Type}).", _path, ex.GetType().Name);
                _cache = new CodexSettings(null, null);
            }
            return _cache;
        }
    }

    public void Save(string? model, string? reasoningEffort)
    {
        var next = new CodexSettings(Blank(model), Blank(reasoningEffort));
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(next, Json));
            File.Move(temp, _path, overwrite: true);
            _cache = next;
        }
    }

    private static string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}

/// <summary>Codex's recent use. Memory only (§0.1).</summary>
public sealed class CodexActivity : SubscriptionActivity;
