using TradingAgent.Config;
using TradingAgent.Persistence;

namespace TradingAgent.Watchlist;

/// <summary>
/// What the monitor may ACT on when the watchlist is the execution universe: alerts and armed orders
/// only for stocks on the watchlist (owner, 2026-11-24).
///
/// <para>
/// <see cref="MonitoredUniverse.ForMonitoringAsync"/> also folds in the configured AllowedSymbols, which in
/// Watchlist mode is only the first-run seed. A stock the user removed from the watchlist therefore kept
/// raising alerts for good, with no row left to mute it on. Charting, scanning and the archive still use the
/// wider set; only these two outputs are narrowed.
/// </para>
///
/// <para>
/// An armed order on a stock off the watchlist is KEPT, not cancelled: it simply does not fire until the
/// stock is back on the list, and the armed-orders panel says so. The one exception is a protective stop's
/// local backstop (<see cref="ArmedOrder.ProtectiveStopId"/>): it is not an order the user armed but the
/// safety net under a position they hold, and leaving that position bare is worse than any surprise.
/// </para>
/// </summary>
public static class WatchlistScope
{
    /// <summary>
    /// The watched set when the watchlist is the execution universe, or null when nothing is narrowed
    /// (the AllowedSymbols source, where the configured list is the tradable universe itself).
    /// </summary>
    public static IReadOnlySet<string>? Watched(
        TradingExecutionUniverseSource source, WatchlistSnapshot watchlist) =>
        source == TradingExecutionUniverseSource.Watchlist
            ? watchlist.Entries
                .Select(entry => entry.Symbol.Trim().ToUpperInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;

    /// <summary>True when an alert for this stock may be raised.</summary>
    public static bool MayAlert(string symbol, IReadOnlySet<string>? watched) =>
        watched is null || watched.Contains(symbol.Trim());

    /// <summary>True when this armed order may be evaluated and fired.</summary>
    public static bool MayFire(ArmedOrder order, IReadOnlySet<string>? watched) =>
        watched is null
        || order.ProtectiveStopId is not null
        || watched.Contains(order.Symbol.Trim());
}
