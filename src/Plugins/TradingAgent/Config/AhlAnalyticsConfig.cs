namespace TradingAgent.Config;

/// <summary>
/// Configuration for the AHL Analytics research portal (<c>data.arifhabibltd.com</c>) — a
/// SEPARATE product from the trading terminal, reached by an SSO handshake through the broker
/// session. See <c>docs/ahl-analytics-api.md</c> for the captured protocol.
///
/// <para>
/// <b>This portal is read-only research.</b> It carries no order path and no L2 depth (verified:
/// every order-book endpoint 500s and the websocket exposes best-bid/ask only). Depth and execution
/// stay with the broker feed. What this adds is breadth — the whole market in one call, five years
/// of candles, fundamentals, and event calendars.
/// </para>
/// </summary>
public sealed class AhlAnalyticsConfig
{
    public const string SectionName = "AhlAnalytics";

    /// <summary>
    /// Off by default, matching how every other broker-touching capability was introduced
    /// (<c>PreferDirectApiForPlacement</c>, the feed before it was verified).
    ///
    /// <para>
    /// Enabling it has one side effect worth stating: the SSO handshake needs a live broker session,
    /// so the first call on a cold session goes through <c>AhkPortalClient.EnsureSessionAsync</c> and
    /// can therefore trigger a browser LOGIN. That is why nothing here runs on a timer — every read
    /// is user- or agent-initiated, and the token it obtains is then good for months.
    /// </para>
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Base URL of the analytics portal. Only overridden if AHL moves the host; the SSO URL the
    /// broker hands us is absolute, so this is used for the API calls that follow it.
    /// </summary>
    public string BaseUrl { get; set; } = "https://data.arifhabibltd.com/";

    /// <summary>
    /// How long a fetched whole-market snapshot stays usable before another
    /// <c>POST /api/v3/market?path=/req</c> is made.
    ///
    /// <para>
    /// The snapshot is ~1.1 MB and covers 857 equities, so a movers screen that re-fetched per
    /// request would move a megabyte to re-sort data it already had. 60s is well inside the
    /// portal's own refresh cadence and keeps a dashboard poll honest. The cache is also what makes
    /// the movers screens free: they are pure sorts over this one payload.
    /// </para>
    /// </summary>
    public int SnapshotCacheSeconds { get; set; } = 60;

    /// <summary>
    /// How long the portal's Bearer token is trusted before the SSO handshake is repeated.
    ///
    /// <para>
    /// The token the portal issues is a Laravel Passport personal access token whose <c>exp</c> is
    /// about a YEAR out, and the SSO blob that mints it is replayable and returns the same token.
    /// So this is not a session timeout — it is a deliberately conservative re-handshake interval
    /// so a revoked or rotated token is picked up in days rather than at the year boundary. A 401
    /// re-handshakes immediately regardless (see <c>AhlAnalyticsClient</c>), so this value only
    /// bounds the case where the token stops working WITHOUT a 401.
    /// </para>
    /// </summary>
    public int TokenLifetimeHours { get; set; } = 72;

    /// <summary>
    /// Requests per minute to stay under. The portal answers <c>X-RateLimit-Limit: 60</c> and — this
    /// is the trap — punishes a burst with <c>401 {"message":"Unauthenticated."}</c> rather than 429.
    ///
    /// <para>
    /// A client that read that 401 as "token dead" would re-run the SSO handshake on every throttle,
    /// which on a cold broker session means a LOGIN per throttle. So the limiter exists to keep us
    /// away from the cliff, and the 401 handler backs off before it re-authenticates. Set below 60
    /// to leave headroom for the operator's own browser session sharing the quota.
    /// </para>
    /// </summary>
    public int MaxRequestsPerMinute { get; set; } = 40;

    /// <summary>
    /// Cross-check the portal's precomputed <c>/api/v3/indicators</c> against locally computed ones
    /// instead of preferring the local computation. Off by default, and the default is the safe one:
    /// the two DISAGREE materially (MACFL on 2026-08-19: portal CCI −34.27, computed from the same
    /// vendor's candles 215.23; RSI 78.22 vs 76.84). The portal's own UI ignores that endpoint and
    /// computes from candles, which is the behaviour this mirrors.
    /// </summary>
    public bool PreferPortalIndicators { get; set; }

    /// <summary>
    /// Symbols whose fundamentals are known to be misleading because the API serves ONLY
    /// unconsolidated statements (the <c>consolidated</c> query parameter is accepted and ignored).
    /// For a holding company the difference is not cosmetic: LUCK's FY26 PAT is 46.6bn
    /// unconsolidated against 89bn consolidated. Listing a symbol here makes the dossier say so
    /// rather than quoting half the earnings as fact.
    /// </summary>
    public List<string> ConsolidationWarningSymbols { get; set; } =
        ["LUCK", "ENGROH", "PKGS", "HUBC", "TPL", "AHCL", "GAL", "ATRL"];
}
