using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentFox.Plugins.Interfaces;
using Microsoft.Extensions.Logging;

namespace AgentFox.Learning;

public sealed record ExperienceAttempt(
    int Number,
    string ToolName,
    Dictionary<string, object?> Arguments,
    bool Success,
    string Outcome,
    DateTime TimestampUtc);

public sealed class LearnedExperience
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Task { get; set; } = string.Empty;
    public string SourceAgent { get; set; } = string.Empty;
    public List<ExperienceAttempt> Attempts { get; set; } = new();
    public string SuccessfulStrategy { get; set; } = string.Empty;
    public List<string> FailureLessons { get; set; } = new();
    public double Confidence { get; set; } = 0.6;
    public int SuccessCount { get; set; } = 1;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public interface IExperienceStore
{
    Task<IReadOnlyList<LearnedExperience>> SearchAsync(string task, int limit = 3, CancellationToken ct = default);
    Task SaveAsync(LearnedExperience experience, CancellationToken ct = default);
    Task<IReadOnlyList<LearnedExperience>> GetAllAsync(CancellationToken ct = default);
}

/// <summary>A small, human-inspectable durable store. Writes are atomic and serialized.</summary>
public sealed class JsonExperienceStore : IExperienceStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public JsonExperienceStore(string path) => _path = path;

    public async Task<IReadOnlyList<LearnedExperience>> GetAllAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { return await ReadUnsafeAsync(ct); }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<LearnedExperience>> SearchAsync(string task, int limit = 3, CancellationToken ct = default)
    {
        var query = Tokens(task);
        if (query.Count == 0) return [];
        var all = await GetAllAsync(ct);
        return all.Select(x => (Experience: x, Score: Similarity(query, Tokens(x.Task))))
            .Where(x => x.Score >= 0.15)
            .OrderByDescending(x => x.Score * (0.5 + x.Experience.Confidence))
            .ThenByDescending(x => x.Experience.UpdatedAtUtc)
            .Take(Math.Clamp(limit, 1, 10))
            .Select(x => x.Experience)
            .ToList();
    }

    public async Task SaveAsync(LearnedExperience experience, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var all = await ReadUnsafeAsync(ct);
            var signature = Signature(experience);
            var existing = all.FirstOrDefault(x => Signature(x) == signature);
            if (existing is null) all.Add(experience);
            else
            {
                existing.SuccessCount++;
                existing.Confidence = Math.Min(0.95, existing.Confidence + 0.1);
                existing.UpdatedAtUtc = DateTime.UtcNow;
                existing.Attempts = experience.Attempts;
                existing.FailureLessons = experience.FailureLessons;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
            var temp = _path + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(all, _json), ct);
            File.Move(temp, _path, true);
        }
        finally { _gate.Release(); }
    }

    private async Task<List<LearnedExperience>> ReadUnsafeAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return [];
        try
        {
            var json = await File.ReadAllTextAsync(_path, ct);
            return JsonSerializer.Deserialize<List<LearnedExperience>>(json, _json) ?? [];
        }
        catch (JsonException) { return []; }
    }

    private static string Signature(LearnedExperience x) =>
        string.Join('|', Tokens(x.Task).Order()) + "::" +
        string.Join('>', x.Attempts.Where(a => a.Success).Select(a => a.ToolName));

    private static HashSet<string> Tokens(string value) => Regex.Matches(value.ToLowerInvariant(), "[a-z0-9_]{3,}")
        .Select(m => m.Value).Where(x => !StopWords.Contains(x)).ToHashSet();

    private static double Similarity(HashSet<string> left, HashSet<string> right) =>
        right.Count == 0 ? 0 : left.Intersect(right).Count() / (double)left.Union(right).Count();

    private static readonly HashSet<string> StopWords = new(["the", "and", "for", "with", "that", "this", "from", "into", "can", "how"]);
}

public sealed class ExperienceTurn(string task, string agentName)
{
    public string Task { get; } = task;
    public string AgentName { get; } = agentName;
    public List<ExperienceAttempt> Attempts { get; } = new();
    internal ExperienceTurn? Parent { get; set; }
}

/// <summary>Captures verified tool outcomes and promotes failure-to-success traces into shared guidance.</summary>
public sealed class ExperienceLearningService
{
    private readonly IExperienceStore _store;
    private readonly ILogger<ExperienceLearningService>? _logger;
    private readonly AsyncLocal<ExperienceTurn?> _currentTurn = new();

    public ExperienceLearningService(IExperienceStore store, ILogger<ExperienceLearningService>? logger = null)
    {
        _store = store;
        _logger = logger;
    }

    public ExperienceTurn BeginTurn(string task, string agentName)
    {
        var turn = new ExperienceTurn(task, agentName) { Parent = _currentTurn.Value };
        _currentTurn.Value = turn;
        return turn;
    }

    public void EndTurn(ExperienceTurn? turn)
    {
        if (turn != null && ReferenceEquals(_currentTurn.Value, turn))
            _currentTurn.Value = turn.Parent;
    }

    public void RecordCurrent(string toolName, Dictionary<string, object?> arguments, ToolResult result) =>
        Record(_currentTurn.Value, toolName, arguments, result);

    public void Record(ExperienceTurn? turn, string toolName, Dictionary<string, object?> arguments, ToolResult result)
    {
        if (turn is null) return;
        turn.Attempts.Add(new ExperienceAttempt(turn.Attempts.Count + 1, toolName,
            Redact(arguments), result.Success, Truncate(result.Success ? result.Output : result.Error), DateTime.UtcNow));
    }

    public async Task CompleteAsync(ExperienceTurn? turn, bool turnSucceeded, CancellationToken ct = default)
    {
        if (turn is null || !turnSucceeded || !turn.Attempts.Any(a => !a.Success)) return;
        var lastSuccess = turn.Attempts.FindLastIndex(a => a.Success);
        if (lastSuccess < 0 || !turn.Attempts.Take(lastSuccess).Any(a => !a.Success)) return;

        var successful = turn.Attempts.Skip(lastSuccess).Where(a => a.Success).ToList();
        var failures = turn.Attempts.Take(lastSuccess).Where(a => !a.Success).ToList();
        var experience = new LearnedExperience
        {
            Task = turn.Task,
            SourceAgent = turn.AgentName,
            Attempts = turn.Attempts,
            SuccessfulStrategy = string.Join(" -> ", successful.Select(a => $"{a.ToolName}({FormatArgs(a.Arguments)})")),
            FailureLessons = failures.Select(a => $"{a.ToolName} failed: {a.Outcome}").Distinct().ToList()
        };
        await _store.SaveAsync(experience, ct);
        _logger?.LogInformation("Learned shared experience {ExperienceId} from agent {AgentName}", experience.Id, turn.AgentName);
    }

    public async Task<string> BuildBaselineAsync(string task, CancellationToken ct = default)
    {
        var matches = await _store.SearchAsync(task, 3, ct);
        if (matches.Count == 0) return string.Empty;
        var sb = new StringBuilder("[Shared learned baseline from previously verified attempts:]\n");
        foreach (var x in matches)
        {
            sb.AppendLine($"- Strategy: {x.SuccessfulStrategy}");
            foreach (var failure in x.FailureLessons.Take(2)) sb.AppendLine($"  Avoid: {failure}");
            sb.AppendLine($"  Evidence: {x.SuccessCount} successful run(s), confidence {x.Confidence:F2}, source {x.SourceAgent}.");
        }
        sb.AppendLine("Use this only when current preconditions match, and verify the result again.\n");
        return sb.ToString();
    }

    private static Dictionary<string, object?> Redact(Dictionary<string, object?> args) => args.ToDictionary(
        x => x.Key,
        x => Regex.IsMatch(x.Key, "token|secret|password|key|credential|authorization", RegexOptions.IgnoreCase) ? "[REDACTED]" : x.Value);

    private static string FormatArgs(Dictionary<string, object?> args) => string.Join(", ", args.Take(4).Select(x => $"{x.Key}={x.Value}"));
    private static string Truncate(string? value) => string.IsNullOrWhiteSpace(value) ? "No details" : value.Length <= 500 ? value : value[..500];
}
