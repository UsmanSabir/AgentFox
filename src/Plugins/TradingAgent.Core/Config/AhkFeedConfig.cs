namespace TradingAgent.Config;

/// <summary>
/// The AHK portal's live quote feed (<c>GET /Home/GetFeed</c>). See <c>docs/ahk-feed-api.md</c> for
/// the captured protocol this binds to.
///
/// <para>
/// The feed is strictly better than the PSX portal on latency and on content — it carries best
/// bid/ask, which PSX publishes nowhere — but it runs against the BROKER's infrastructure using the
/// account's own session. That is the whole reason the defaults here are conservative: this is not
/// a public data portal that can be polled freely, it is the same session that places orders.
/// </para>
/// </summary>
public sealed class AhkFeedConfig
{
    public const string SectionName = "AhkFeed";

    /// <summary>
    /// On by default, now that the feed has been verified live end to end.
    ///
    /// <para>
    /// Enabling it has a side effect beyond data: during market hours the feed is usually the first
    /// consumer to request a broker session. Once requested, <see cref="Feed.AhkSessionRecoveryWorker"/>
    /// keeps that same session alive independently; it does not create repeated logins. The session is
    /// the same one the order path uses, which is why the poll cadence is pinned to the portal's own
    /// and why the worker yields while the browser holds the trading screen.
    /// </para>
    ///
    /// <para>
    /// Turning it off is always safe and never loses a capability: every consumer falls back to the
    /// PSX market watch, exactly as before this feed existed. What is lost is freshness and depth —
    /// PSX publishes no bid/ask at all. Switch it off for order-only work against a rate-sensitive
    /// account, where the whole place/read/cancel/verify cycle is about seven requests and background
    /// polling would dominate the traffic.
    /// </para>
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Seconds between <c>GetFeed</c> polls. The portal's own UI polls every 1–2s, so 2s is parity
    /// with a human sitting at the terminal and is the floor enforced here. Polling faster than the
    /// vendor's own client is the behaviour most likely to get an account flagged, and it buys
    /// nothing: the exchange does not tick faster than the portal publishes.
    /// </summary>
    public double PollSeconds { get; set; } = 2.0;

    /// <summary>
    /// Seconds between background <c>POST /Home/Relogin</c> calls, which renew the existing session
    /// before expiry. The portal's UI does this about once a minute. This is a keepalive, not LOGIN.
    /// </summary>
    public double ReloginSeconds { get; set; } = 60.0;

    /// <summary>
    /// Symbols per <c>SendSubscriptionofSymbols</c> page. The portal's own client uses 50 and pages
    /// through anything larger; whether that is a server limit or just its table size is unknown
    /// (see docs), so the safe move is to copy it rather than discover the ceiling in production.
    /// </summary>
    public int SymbolsPerPage { get; set; } = 50;

    /// <summary>
    /// Subscription slots to spread the universe across, using the portal's own page names
    /// (<c>Page1</c>…<c>Page4</c>). Empty means the default four, which at the default page size caps
    /// the feed at 200 symbols — comfortably more than a watchlist, far less than the whole market.
    /// Symbols beyond the cap are served from PSX instead, with a warning naming them.
    ///
    /// <para>
    /// <b>Deliberately left empty rather than pre-populated.</b> .NET's <c>ConfigurationBinder</c>
    /// APPENDS to a collection property that already has items instead of replacing it, so a default
    /// of four names plus four names in appsettings binds to a list of EIGHT — every slot duplicated.
    /// Because slots are overwritten in order, the duplicate re-sends each slot with the (empty)
    /// slice at its index and wipes the subscription the first occurrence just made. That produced a
    /// live run which subscribed 30 symbols and instantly unsubscribed them, with no error anywhere.
    /// The fallback lives in <see cref="Feed.FeedPagePlanner.NormalizePages"/>, which also
    /// de-duplicates whatever it is given.
    /// </para>
    /// </summary>
    public List<string> Pages { get; set; } = [];

    // ── Market depth (MBP / MBO) ──────────────────────────────────────────────

    /// <summary>
    /// Subscribe to market depth for one symbol at a time, in addition to the quote feed.
    ///
    /// <para>
    /// Off by default and deliberately narrow. The portal's own UI shows depth for a single symbol —
    /// one MBO panel and one MBP panel beside the market watch — so depth is requested per symbol, not
    /// per watchlist. Enabling this adds one subscription call when the focused symbol changes and
    /// costs nothing per poll, since the depth arrays ride along on the <c>GetFeed</c> response the
    /// quote feed is already reading.
    /// </para>
    /// </summary>
    public bool DepthEnabled { get; set; }

    /// <summary>
    /// The page slot used for the depth subscription. <b>It must not appear in <see cref="Pages"/>.</b>
    ///
    /// <para>
    /// This is the sharp edge. <c>pagenum</c> is a single namespace shared by every feed type, and a
    /// subscription REPLACES whatever the slot held. Sending a depth subscription for a page the quote
    /// feed is using therefore evicts that page's market-feed symbols — and the portal reports nothing:
    /// <c>GetFeed</c> answers 200 with an empty array whether nothing traded or nothing is subscribed,
    /// so the only symptom is quotes quietly ceasing for a quarter of the universe. The subscription is
    /// refused outright on overlap rather than trusting configuration to be careful.
    /// </para>
    ///
    /// <para>
    /// The default quote pages are Page1–Page4 (4 x 50 = 200 symbols), so a depth page has to come from
    /// outside that set, or one of them has to be given up. Page5 is the verified default: on
    /// 2026-08-20 the running plugin focused PPL there and received 10 MBP plus 10 MBO rows while all
    /// 30 quote symbols on Page1 remained fresh.
    /// </para>
    /// </summary>
    public string DepthPage { get; set; } = "Page5";

    /// <summary>
    /// The market board to subscribe on. <c>REG</c> is the regular board; <c>ODL</c> is odd-lot and
    /// carries different (thinner) prices, so mixing them would silently blend two order books.
    /// </summary>
    public string Market { get; set; } = "REG";

    /// <summary>
    /// How old a quote may be before <see cref="Research.AhkQuoteSource"/> stops serving it. A
    /// polled feed that dies looks exactly like a market with no trades — both are "no new data" —
    /// and serving the last known price forever is how a stop-loss ends up evaluated against a
    /// number from two hours ago. Past this age the symbol is dropped from the snapshot and the PSX
    /// fallback covers it. Must comfortably exceed the quiet periods of an illiquid symbol, hence
    /// minutes rather than seconds.
    /// </summary>
    public double MaxQuoteAgeSeconds { get; set; } = 600.0;

    /// <summary>
    /// Consecutive poll failures tolerated before the feed reports itself unhealthy and the
    /// composite falls back to PSX. Transient 500s from the portal are routine; a sustained run of
    /// them is a dead session.
    /// </summary>
    public int UnhealthyAfterConsecutiveFailures { get; set; } = 5;

    /// <summary>
    /// Poll only while the market is open (plus <see cref="PrePostMarketMinutes"/> either side).
    /// The feed publishes nothing overnight, so polling it would be pure load on the broker for no
    /// data — and it would hold a session open around the clock.
    /// </summary>
    public bool OnlyDuringMarketHours { get; set; } = true;

    /// <summary>Minutes before the open and after the close to keep polling. Covers the pre-open session.</summary>
    public int PrePostMarketMinutes { get; set; } = 30;

    /// <summary>
    /// Consecutive empty polls, during a market the PORTAL reports as open, after which the
    /// subscription is re-sent (minimum 5).
    ///
    /// <para>
    /// This exists because a lost subscription is invisible. The portal answers HTTP 200 with an
    /// empty feed whether nothing has traded or nothing is subscribed, and it never volunteers which.
    /// The portal's own page load re-subscribes Page1 from its (usually empty) market-watch table, so
    /// merely opening the trading screen to place an order wipes the subscription out from under this
    /// worker. That specific case is caught directly, by re-subscribing when the browser releases the
    /// screen; this counter is the backstop for every other way it can happen. At the default
    /// 2s poll, 30 polls is a minute of total silence across the whole watchlist — which does not
    /// occur in an open market — and re-subscribing costs a single POST.
    /// </para>
    /// </summary>
    public int ResubscribeAfterSilentPolls { get; set; } = 30;
}
