using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace TradingAgent.Research;

/// <summary>Official PSX index membership from the data portal's constituent tables.</summary>
public sealed partial class PsxDataClient
{
    private readonly ConcurrentDictionary<string, (DateTime RetrievedAtUtc, IReadOnlyList<string> Symbols)>
        _indexConstituents = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _indexConstituentGate = new(1, 1);
    private static readonly TimeSpan IndexConstituentTtl = TimeSpan.FromMinutes(15);
    private static readonly Regex IndexConstituentTableRegex = new(
        @"INDEX\s+Constituents</h2>[\s\S]*?<table[^>]*>(?<table>[\s\S]*?)</table>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex IndexCompanyLinkRegex = new(
        @"href\s*=\s*[""']/?company/(?<symbol>[A-Z0-9_-]+)[""']",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task<PsxIndexConstituents> GetIndexConstituentsAsync(
        string index, CancellationToken ct = default)
    {
        index = NormalizePortalSymbol(index, "index");
        var baseUrl = _options.Value.PsxDataBaseUrl.TrimEnd('/');
        var sourceUrl = $"{baseUrl}/indices/{index}";

        if (_indexConstituents.TryGetValue(index, out var cached)
            && DateTime.UtcNow - cached.RetrievedAtUtc < IndexConstituentTtl)
        {
            return new(index, cached.Symbols, cached.RetrievedAtUtc, sourceUrl);
        }

        await _indexConstituentGate.WaitAsync(ct);
        try
        {
            if (_indexConstituents.TryGetValue(index, out cached)
                && DateTime.UtcNow - cached.RetrievedAtUtc < IndexConstituentTtl)
            {
                return new(index, cached.Symbols, cached.RetrievedAtUtc, sourceUrl);
            }

            try
            {
                var html = await _http.GetStringAsync(sourceUrl, ct);
                var symbols = ParseIndexConstituentTable(html);
                if (symbols.Count == 0)
                    return new(index, [], DateTime.UtcNow, sourceUrl,
                        "The official PSX index page returned no recognizable constituents.");

                var retrievedAtUtc = DateTime.UtcNow;
                _indexConstituents[index] = (retrievedAtUtc, symbols);
                return new(index, symbols, retrievedAtUtc, sourceUrl);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "[PsxData] Index constituents failed for {Index}.", index);
                return new(index, [], DateTime.UtcNow, sourceUrl,
                    $"The official PSX index page could not be read: {ex.Message}");
            }
        }
        finally
        {
            _indexConstituentGate.Release();
        }
    }

    /// <summary>
    /// Parses the portal table by its semantic <c>data-name="symbol"</c> header. Keeping this public
    /// makes a portal markup change independently testable without a live network request.
    /// </summary>
    public static IReadOnlyList<string> ParseIndexConstituentTable(string? html)
    {
        var semantic = ParseDataTable(html)
            .Select(row => row.GetValueOrDefault("symbol")?.Trim().ToUpperInvariant() ?? "")
            .Where(symbol => symbol.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(symbol => symbol, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (semantic.Count > 0) return semantic;

        // Unlike market-watch, the current constituent table has visible headers only. Scope the
        // fallback to the table immediately following "INDEX Constituents" before reading its
        // official /company/{ticker} links, so unrelated company links elsewhere on the page cannot
        // leak into membership.
        if (string.IsNullOrWhiteSpace(html)
            || IndexConstituentTableRegex.Match(html) is not { Success: true } table)
            return [];

        return IndexCompanyLinkRegex.Matches(table.Groups["table"].Value)
            .Select(match => match.Groups["symbol"].Value.ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(symbol => symbol, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

public sealed record PsxIndexConstituents(
    string Index,
    IReadOnlyList<string> Symbols,
    DateTime RetrievedAtUtc,
    string SourceUrl,
    string? Error = null);
