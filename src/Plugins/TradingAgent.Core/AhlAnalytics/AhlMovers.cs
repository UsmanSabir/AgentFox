using System.Globalization;

namespace TradingAgent.AhlAnalytics;

/// <summary>
/// Market-mover screens derived from the whole-market snapshot. Pure computation — no I/O of its own,
/// so every screen below costs one shared snapshot fetch no matter how many are requested.
///
/// <para>
/// <b>The freshness filter is the whole trick, not the sort.</b> The snapshot contains every listed
/// symbol including ones dormant for months, each carrying a stale-but-plausible percent change: on
/// 2026-08-19 the rights instrument <c>786R</c> sat in the payload showing −6.44% from a tick dated
/// 2026-01-02. Rank without comparing each row's own tick date against the market's last-update date
/// and a "today's biggest movers" list fills with long-dead instruments. The portal's own dashboard
/// applies exactly this filter before sorting, and it is reproduced here for the same reason.
/// </para>
///
/// <para>
/// Beyond reproducing the portal's three lists, these screens add what a trader actually needs and
/// the portal does not show: unusual volume against the 10-day average, gaps against the previous
/// close, proximity to the session's circuit caps, and universe restriction so a list is not topped
/// by names too illiquid to trade.
/// </para>
/// </summary>
public static class AhlMovers
{
    /// <summary>
    /// One row in a movers list. Deliberately flat and pre-computed so both the agent tool and the
    /// dashboard render the same numbers without either re-deriving them.
    /// </summary>
    public sealed record MoverRow(
        string Symbol,
        string? Name,
        string? SectorCode,
        string? Sector,
        decimal? Price,
        decimal? Change,
        decimal? ChangePercent,
        long? Volume,
        decimal? TurnoverPkr,
        decimal? VolumeVsAvg10Day,
        decimal? GapPercent,
        decimal? Rsi,
        decimal? DistanceToUpperCapPercent,
        decimal? DistanceToLowerLockPercent,
        bool AtUpperCap,
        bool AtLowerLock,
        long? FreeFloat,
        decimal? DividendYieldPercent,
        IReadOnlyList<string>? Indices,
        bool ExDividend,
        bool ExBonus,
        bool ExRights,
        string? LastTickAt);

    /// <summary>Which screen to run.</summary>
    public enum Screen
    {
        /// <summary>Biggest percentage gainers.</summary>
        Gainers,
        /// <summary>Biggest percentage losers.</summary>
        Losers,
        /// <summary>Most active by share volume — the portal calls this "Leaders".</summary>
        MostActive,
        /// <summary>Most active by traded value (price × volume), which ranks differently from volume
        /// and is usually the more meaningful "where is the money" list on PSX, where penny names
        /// dominate share counts.</summary>
        MostValuable,
        /// <summary>Volume most elevated against its own 10-day average — the continuation screen.</summary>
        UnusualVolume,
        /// <summary>Largest upward gap from the previous close.</summary>
        GapUp,
        /// <summary>Largest downward gap from the previous close.</summary>
        GapDown,
        /// <summary>Trading at or nearest to the session's upper cap.</summary>
        NearUpperCap,
        /// <summary>Trading at or nearest to the session's lower lock.</summary>
        NearLowerLock
    }

    /// <summary>Filters applied before any screen sorts.</summary>
    public sealed record Filter(
        /// <summary>Restrict to members of this index, e.g. <c>KSE100</c>. Null means all equities.</summary>
        string? Index = null,
        /// <summary>Restrict to this PSX sector code, e.g. <c>0804</c>.</summary>
        string? SectorCode = null,
        /// <summary>Minimum traded value in PKR — the practical liquidity floor.</summary>
        decimal? MinTurnoverPkr = null,
        /// <summary>Minimum share volume.</summary>
        long? MinVolume = null,
        /// <summary>Minimum last price, to exclude sub-rupee names where a tick is a large percentage.</summary>
        decimal? MinPrice = null);

    /// <summary>
    /// Runs one screen. Returns an empty list rather than throwing when the snapshot is unusable —
    /// callers are a tool and a dashboard.
    /// </summary>
    public static IReadOnlyList<MoverRow> Run(
        AhlSnapshotData? snapshot, Screen screen, int limit = 10, Filter? filter = null)
    {
        if (snapshot?.Equities is null or { Count: 0 }) return [];

        var rows = FreshRows(snapshot, filter);
        if (rows.Count == 0) return [];

        IEnumerable<MoverRow> ordered = screen switch
        {
            Screen.Gainers       => rows.Where(r => r.ChangePercent > 0)
                                        .OrderByDescending(r => r.ChangePercent),
            Screen.Losers        => rows.Where(r => r.ChangePercent < 0)
                                        .OrderBy(r => r.ChangePercent),
            Screen.MostActive    => rows.OrderByDescending(r => r.Volume ?? 0),
            Screen.MostValuable  => rows.OrderByDescending(r => r.TurnoverPkr ?? 0),
            // Require a real average to divide by, or a symbol whose 10-day average is ~0 (a name that
            // barely trades) ranks first on a single lot — the exact opposite of "unusual volume worth
            // acting on".
            Screen.UnusualVolume => rows.Where(r => r.VolumeVsAvg10Day is > 1 && r.Volume > 0)
                                        .OrderByDescending(r => r.VolumeVsAvg10Day),
            Screen.GapUp         => rows.Where(r => r.GapPercent > 0)
                                        .OrderByDescending(r => r.GapPercent),
            Screen.GapDown       => rows.Where(r => r.GapPercent < 0)
                                        .OrderBy(r => r.GapPercent),
            Screen.NearUpperCap  => rows.Where(r => r.DistanceToUpperCapPercent is not null)
                                        .OrderBy(r => r.DistanceToUpperCapPercent),
            Screen.NearLowerLock => rows.Where(r => r.DistanceToLowerLockPercent is not null)
                                        .OrderBy(r => r.DistanceToLowerLockPercent),
            _ => rows
        };

        return ordered.Take(Math.Clamp(limit, 1, 100)).ToList();
    }

    /// <summary>
    /// Sector rotation: each sector's median percent change and total turnover for the session,
    /// strongest first. Median rather than mean because one halted or capped name would otherwise
    /// carry a whole sector.
    /// </summary>
    public sealed record SectorMove(
        string SectorCode,
        string? SectorName,
        int Symbols,
        decimal MedianChangePercent,
        decimal TotalTurnoverPkr,
        int Advancing,
        int Declining);

    public static IReadOnlyList<SectorMove> SectorRotation(
        AhlSnapshotData? snapshot, Filter? filter = null)
    {
        if (snapshot?.Equities is null) return [];

        return FreshRows(snapshot, filter)
            .Where(r => r.SectorCode is not null && r.ChangePercent is not null)
            .GroupBy(r => r.SectorCode!)
            .Where(g => g.Count() >= 2) // a one-symbol "sector" median is just that symbol
            .Select(g =>
            {
                var changes = g.Select(r => r.ChangePercent!.Value).OrderBy(x => x).ToList();
                return new SectorMove(
                    SectorCode: g.Key,
                    SectorName: AhlSectors.Name(g.Key) ?? g.Key,
                    Symbols: changes.Count,
                    MedianChangePercent: Round(Median(changes), 2) ?? 0m,
                    TotalTurnoverPkr: g.Sum(r => r.TurnoverPkr ?? 0),
                    Advancing: g.Count(r => r.ChangePercent > 0),
                    Declining: g.Count(r => r.ChangePercent < 0));
            })
            .OrderByDescending(s => s.MedianChangePercent)
            .ToList();
    }

    /// <summary>
    /// Market breadth for the session — advancing versus declining, and how much turnover sits on
    /// each side. Breadth diverging from the index is the classic warning a rally is narrow.
    /// </summary>
    public sealed record Breadth(
        string? MarketState,
        string? LastUpdate,
        int TradedToday,
        int TotalListed,
        int Advancing,
        int Declining,
        int Unchanged,
        int AtUpperCap,
        int AtLowerLock,
        decimal TotalTurnoverPkr,
        decimal AdvancingTurnoverPkr);

    public static Breadth? MarketBreadth(AhlSnapshotData? snapshot)
    {
        if (snapshot?.Equities is null) return null;

        var rows = FreshRows(snapshot, null);
        return new Breadth(
            MarketState: snapshot.MarketState,
            LastUpdate: snapshot.LastUpdate,
            TradedToday: rows.Count,
            TotalListed: snapshot.Equities.Count,
            Advancing: rows.Count(r => r.ChangePercent > 0),
            Declining: rows.Count(r => r.ChangePercent < 0),
            Unchanged: rows.Count(r => r.ChangePercent == 0),
            AtUpperCap: rows.Count(r => r.AtUpperCap),
            AtLowerLock: rows.Count(r => r.AtLowerLock),
            TotalTurnoverPkr: rows.Sum(r => r.TurnoverPkr ?? 0),
            AdvancingTurnoverPkr: rows.Where(r => r.ChangePercent > 0).Sum(r => r.TurnoverPkr ?? 0));
    }

    // ── internals ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Projects the snapshot's equities to rows, keeping only those that traded in the market's most
    /// recent session and pass <paramref name="filter"/>. See the class remarks for why the date
    /// comparison is load-bearing.
    /// </summary>
    private static List<MoverRow> FreshRows(AhlSnapshotData snapshot, Filter? filter)
    {
        var sessionDate = DatePart(snapshot.LastUpdate);
        var rows = new List<MoverRow>();

        foreach (var (symbol, eq) in snapshot.Equities!)
        {
            // Freshness. When the snapshot itself carries no last-update stamp we cannot tell fresh
            // from stale, so nothing is admitted — a silently stale movers list is worse than none.
            if (sessionDate is null) return [];
            if (DatePart(eq.LastTickAt) != sessionDate) continue;

            if (filter?.Index is { Length: > 0 } wanted &&
                (eq.ListedIn is null ||
                 !eq.ListedIn.Contains(wanted, StringComparer.OrdinalIgnoreCase)))
                continue;

            if (filter?.SectorCode is { Length: > 0 } sector &&
                !string.Equals(eq.SectorCode, sector, StringComparison.OrdinalIgnoreCase))
                continue;

            var price = eq.Close;
            if (filter?.MinPrice is { } minPrice && (price ?? 0) < minPrice) continue;
            if (filter?.MinVolume is { } minVol && (eq.Volume ?? 0) < minVol) continue;

            var turnover = price is not null && eq.Volume is not null
                ? price.Value * eq.Volume.Value
                : (decimal?)null;
            if (filter?.MinTurnoverPkr is { } minTurnover && (turnover ?? 0) < minTurnover) continue;

            // `pch` is a fraction on the wire; every consumer here wants percent.
            var changePercent = eq.ChangeFraction is not null
                ? eq.ChangeFraction.Value * 100m
                : (decimal?)null;

            var volVsAvg = eq.AvgVolume10Day is > 0 && eq.Volume is not null
                ? eq.Volume.Value / eq.AvgVolume10Day.Value
                : (decimal?)null;

            // Gap is today's OPEN against the PREVIOUS close — `ldcp`, not `close`, which is today's.
            var gapPercent = eq.PreviousClose is > 0 && eq.Open is > 0
                ? (eq.Open.Value - eq.PreviousClose.Value) / eq.PreviousClose.Value * 100m
                : (decimal?)null;

            var toUpper = eq.UpperCap is > 0 && price is > 0
                ? (eq.UpperCap.Value - price.Value) / eq.UpperCap.Value * 100m
                : (decimal?)null;
            var toLower = eq.LowerLock is > 0 && price is > 0
                ? (price.Value - eq.LowerLock.Value) / eq.LowerLock.Value * 100m
                : (decimal?)null;

            rows.Add(new MoverRow(
                Symbol: symbol,
                Name: eq.Name,
                SectorCode: eq.SectorCode,
                Sector: AhlSectors.Name(eq.SectorCode) ?? eq.SectorCode,
                Price: price,
                Change: eq.Change,
                ChangePercent: Round(changePercent, 2),
                Volume: eq.Volume,
                TurnoverPkr: Round(turnover, 0),
                VolumeVsAvg10Day: Round(volVsAvg, 2),
                GapPercent: Round(gapPercent, 2),
                Rsi: Round(eq.Rsi, 1),
                DistanceToUpperCapPercent: Round(toUpper, 2),
                DistanceToLowerLockPercent: Round(toLower, 2),
                // Treat "within a tick of the cap" as at it: an order above the cap is refused, so the
                // actionable fact is that there is no headroom, not that there is 0.01 of it.
                AtUpperCap: toUpper is not null && toUpper <= 0.05m,
                AtLowerLock: toLower is not null && toLower <= 0.05m,
                FreeFloat: eq.FreeFloat is not null ? (long)eq.FreeFloat.Value : null,
                DividendYieldPercent: Round(eq.DividendYieldPercent, 2),
                Indices: eq.ListedIn,
                ExDividend: eq.ExDividend ?? false,
                ExBonus: eq.ExBonus ?? false,
                ExRights: eq.ExRights ?? false,
                LastTickAt: eq.LastTickAt));
        }

        return rows;
    }

    /// <summary>Extracts the <c>yyyy-MM-dd</c> prefix of a portal timestamp.</summary>
    private static string? DatePart(string? timestamp) =>
        timestamp is { Length: >= 10 } ? timestamp[..10] : null;

    private static decimal Median(List<decimal> sorted) =>
        sorted.Count == 0 ? 0m
        : sorted.Count % 2 == 1 ? sorted[sorted.Count / 2]
        : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2m;

    private static decimal? Round(decimal? value, int places) =>
        value is null ? null : Math.Round(value.Value, places, MidpointRounding.AwayFromZero);

    /// <summary>Parses a screen name from a tool argument, tolerating the names a model is likely to
    /// reach for. Returns null when unrecognised so the caller can list the valid ones.</summary>
    public static Screen? ParseScreen(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "gainers" or "top_gainers" or "gainer" => Screen.Gainers,
        "losers" or "top_losers" or "loser" => Screen.Losers,
        "most_active" or "active" or "leaders" or "volume" => Screen.MostActive,
        "most_valuable" or "value" or "turnover" => Screen.MostValuable,
        "unusual_volume" or "unusual" or "volume_spike" => Screen.UnusualVolume,
        "gap_up" or "gapup" => Screen.GapUp,
        "gap_down" or "gapdown" => Screen.GapDown,
        "near_upper_cap" or "upper_cap" or "upper_lock" => Screen.NearUpperCap,
        "near_lower_lock" or "lower_lock" => Screen.NearLowerLock,
        _ => null
    };

    /// <summary>The screen names accepted by <see cref="ParseScreen"/>, for error messages and schemas.</summary>
    public static readonly string[] ScreenNames =
    [
        "gainers", "losers", "most_active", "most_valuable", "unusual_volume",
        "gap_up", "gap_down", "near_upper_cap", "near_lower_lock"
    ];

    /// <summary>Formats a turnover figure the way a PSX desk reads it.</summary>
    public static string FormatPkr(decimal? value) => value switch
    {
        null => "-",
        >= 1_000_000_000m => (value.Value / 1_000_000_000m).ToString("0.##", CultureInfo.InvariantCulture) + "bn",
        >= 1_000_000m => (value.Value / 1_000_000m).ToString("0.##", CultureInfo.InvariantCulture) + "mn",
        >= 1_000m => (value.Value / 1_000m).ToString("0.#", CultureInfo.InvariantCulture) + "k",
        _ => value.Value.ToString("0.##", CultureInfo.InvariantCulture)
    };
}
