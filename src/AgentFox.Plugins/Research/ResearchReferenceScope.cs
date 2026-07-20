namespace AgentFox.Plugins.Research;

/// <summary>
/// Ambient, per-turn collector of <see cref="ResearchReference"/>s. The host opens a scope
/// around an agent turn via <see cref="Begin"/>; any tool executing within that turn's async
/// flow registers URLs through <see cref="Current"/>; the host drains them with
/// <see cref="Snapshot"/> when the turn completes.
///
/// Backed by <see cref="AsyncLocal{T}"/> so the value flows across await boundaries. Because
/// this type lives in AgentFox.Plugins (delegated to the host's default load context), the host
/// and every plugin share one static — a plugin tool writing to <see cref="Current"/> is seen by
/// the host turn that opened the scope.
///
/// v1 limitation: only tools running within the opening turn's async flow contribute. Tools that
/// run on a different lane (background workers, sub-agents with their own <see cref="Begin"/>)
/// collect into their own scope, not the parent's.
/// </summary>
public sealed class ResearchReferenceScope : IDisposable
{
    private static readonly AsyncLocal<ResearchReferenceScope?> _current = new();

    private readonly ResearchReferenceScope? _previous;
    private readonly object _gate = new();
    private readonly List<ResearchReference> _items = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private bool _disposed;

    private ResearchReferenceScope(ResearchReferenceScope? previous) => _previous = previous;

    /// <summary>The scope for the current async flow, or null if none is open.</summary>
    public static ResearchReferenceScope? Current => _current.Value;

    /// <summary>Opens a fresh scope, restoring the previous one when the returned handle is disposed.</summary>
    public static IDisposable Begin()
    {
        var scope = new ResearchReferenceScope(_current.Value);
        _current.Value = scope;
        return scope;
    }

    /// <summary>Registers a source. No-op for null/whitespace or non-http(s) URLs. Dedupes by normalized URL.</summary>
    public void Add(string? url, string? title = null, string? source = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return;

        var key = Normalize(uri);
        lock (_gate)
        {
            if (!_seen.Add(key)) return; // first occurrence wins
            _items.Add(new ResearchReference(url.Trim(), Trim(title), Trim(source)));
        }
    }

    public void AddRange(IEnumerable<ResearchReference> references)
    {
        if (references is null) return;
        foreach (var r in references) Add(r.Url, r.Title, r.Source);
    }

    /// <summary>A stable copy of the references collected so far, in first-seen order.</summary>
    public IReadOnlyList<ResearchReference> Snapshot()
    {
        lock (_gate) return _items.ToArray();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _current.Value = _previous;
    }

    private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // Normalize for dedup: lowercase scheme+host, drop a single trailing slash on the path,
    // keep query. Deliberately conservative so distinct articles are never collapsed.
    private static string Normalize(Uri uri)
    {
        var path = uri.AbsolutePath.Length > 1 ? uri.AbsolutePath.TrimEnd('/') : uri.AbsolutePath;
        return $"{uri.Scheme.ToLowerInvariant()}://{uri.Host.ToLowerInvariant()}{path}{uri.Query}";
    }
}
