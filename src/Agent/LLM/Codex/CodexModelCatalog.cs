using System.Text.Json;

namespace AgentFox.LLM.Codex;

/// <param name="Efforts">The reasoning levels this model accepts, as the CLI names them.</param>
/// <param name="DefaultEffort">What the CLI uses when none is set; null when the catalog does not say.</param>
public sealed record CodexModelInfo(string Id, string Name, IReadOnlyList<string> Efforts, string? DefaultEffort);

/// <summary>
/// The models the signed-in ChatGPT plan offers, as the Codex CLI itself reports them. Read from the CLI's own
/// <c>models_cache.json</c> (which it refreshes on every call) or from <c>codex debug models</c>: neither needs a model
/// call. Not hardcoded, because a plan's models change (MEASURED 2026-11-26: a plan lost its "sol" models after the
/// sign-in, the CLI's default stayed one of them, and every call answered "model is not supported"). Only models the
/// catalog marks <c>visibility: list</c> are offered. Holds no credential.
/// </summary>
public static class CodexModelCatalog
{
    public const string CacheFileName = "models_cache.json";

    /// <summary>A catalog that cannot be read is an empty list, never an error: the card then falls back to typing a name.</summary>
    public static IReadOnlyList<CodexModelInfo> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array) return [];
            var found = new List<(int Priority, CodexModelInfo Info)>();
            foreach (var m in models.EnumerateArray())
            {
                if (Text(m, "visibility") != "list") continue;
                var id = Text(m, "slug");
                if (string.IsNullOrWhiteSpace(id)) continue;
                var efforts = new List<string>();
                if (m.TryGetProperty("supported_reasoning_levels", out var levels) && levels.ValueKind == JsonValueKind.Array)
                    foreach (var l in levels.EnumerateArray())
                        if (Text(l, "effort") is { Length: > 0 } e && !efforts.Contains(e)) efforts.Add(e);
                var priority = m.TryGetProperty("priority", out var p) && p.TryGetInt32(out var n) ? n : int.MaxValue;
                found.Add((priority, new CodexModelInfo(id, Text(m, "display_name") ?? id, efforts, Text(m, "default_reasoning_level"))));
            }
            return found.OrderBy(x => x.Priority).Select(x => x.Info).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static IReadOnlyList<CodexModelInfo> ReadCache(CodexPaths paths)
    {
        try
        {
            var file = Path.Combine(paths.Home, CacheFileName);
            return File.Exists(file) ? Parse(File.ReadAllText(file)) : [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
