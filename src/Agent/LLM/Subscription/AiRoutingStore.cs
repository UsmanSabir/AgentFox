using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AgentFox.LLM.Subscription;

/// <summary>
/// Which provider (<see cref="AiProviders"/>) each purpose (<see cref="AiPurposes"/>) goes to, in
/// <c>plugin-configs/ai-routing.json</c>. A purpose never saved here starts from <paramref name="legacy"/> — the
/// per-purpose Claude Code switches saved before Codex existed — and otherwise from the defaults: trading tasks on
/// Claude Code, chat on the configured AI. Choosing a provider that is not signed in simply leaves the purpose on the
/// configured AI until it is.
///
/// Retention (§0.1): one file, replaced whole on each change, no age limit — it is configuration.
/// </summary>
public sealed class AiRoutingStore
{
    public static readonly IReadOnlyList<string> Purposes = [AiPurposes.Chat, AiPurposes.StockAssessment, AiPurposes.RebuyAdvice];
    public static readonly IReadOnlyList<string> Providers = [AiProviders.Configured, AiProviders.ClaudeCode, AiProviders.Codex];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path;
    private readonly Func<string, bool?> _legacy;
    private readonly ILogger<AiRoutingStore> _logger;
    private readonly object _gate = new();
    private Dictionary<string, string>? _cache;

    /// <param name="legacy">An earlier per-purpose Claude Code switch, when one was saved: true = Claude Code.</param>
    public AiRoutingStore(string path, Func<string, bool?> legacy, ILogger<AiRoutingStore> logger)
    {
        _path = path;
        _legacy = legacy;
        _logger = logger;
    }

    public string ProviderFor(string purpose)
    {
        lock (_gate)
        {
            if (Load().TryGetValue(purpose, out var saved) && Providers.Contains(saved)) return saved;
        }
        return _legacy(purpose) switch
        {
            true => AiProviders.ClaudeCode,
            false => AiProviders.Configured,
            null => purpose == AiPurposes.Chat ? AiProviders.Configured : AiProviders.ClaudeCode,
        };
    }

    public IReadOnlyDictionary<string, string> All() => Purposes.ToDictionary(p => p, ProviderFor);

    public void Set(string purpose, string provider)
    {
        if (!Purposes.Contains(purpose)) throw new ArgumentException($"Unknown purpose '{purpose}'.", nameof(purpose));
        if (!Providers.Contains(provider)) throw new ArgumentException($"Unknown provider '{provider}'.", nameof(provider));
        lock (_gate)
        {
            var next = new Dictionary<string, string>(Load(), StringComparer.Ordinal) { [purpose] = provider };
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(next, Json));
            File.Move(temp, _path, overwrite: true);
            _cache = next;
        }
    }

    private Dictionary<string, string> Load()
    {
        if (_cache is not null) return _cache;
        if (!File.Exists(_path)) return _cache = new(StringComparer.Ordinal);
        try
        {
            _cache = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path), Json)
                     ?? new(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Unreadable falls back to the defaults (the configured AI for chat); the next save replaces the file.
            _logger.LogWarning("[AiRouting] {Path} could not be read ({Type}).", _path, ex.GetType().Name);
            _cache = new(StringComparer.Ordinal);
        }
        return _cache;
    }
}
