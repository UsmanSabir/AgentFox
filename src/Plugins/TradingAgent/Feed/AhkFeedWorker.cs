using AgentFox.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingAgent.Broker;
using TradingAgent.Config;
using TradingAgent.Market;
using TradingAgent.Observability;
using TradingAgent.Watchlist;

namespace TradingAgent.Feed;

/// <summary>
/// The single owner of the AHK live-feed poll loop: subscribes the monitored universe, polls
/// <c>GET /Home/GetFeed</c> on the portal's own cadence, and folds every response into
/// <see cref="AhkQuoteBook"/>. <see cref="Research.AhkQuoteSource"/> then serves reads from that
/// book without touching the network.
///
/// <para>
/// <b>Exactly one poller.</b> The feed may be a drain-once queue (unconfirmed — see
/// <c>docs/ahk-feed-api.md</c>). If it is, a second reader on the same session silently halves what
/// each one sees, with nothing anywhere reporting a problem. So the loop lives here, once, and it
/// yields whenever the browser broker has the trading screen open, because the portal's own
/// <c>site.js</c> polls the same endpoint from that page.
/// </para>
///
/// <para>
/// <b>Nothing here can trade.</b> The worker only reads quotes. It shares a session with the order
/// path, which is precisely why it is written to give that path right of way rather than compete
/// with it.
/// </para>
/// </summary>
public sealed class AhkFeedWorker : BackgroundService
{
    /// <summary>Let the host finish starting before opening a broker session; quotes are never urgent at t=0.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(25);

    /// <summary>How long to idle when the market is shut, or the session is unusable.</summary>
    private static readonly TimeSpan IdlePoll = TimeSpan.FromMinutes(2);

    private readonly AhkPortalClient _portal;
    private readonly AhkQuoteBook _book;
    private readonly AhkDepthBook _depth;
    private readonly AhkBroker _broker;
    private readonly MonitoredUniverse _universe;
    private readonly IMarketCalendar _calendar;
    private readonly IRuntimePluginOptions<AhkFeedConfig> _config;
    private readonly IRuntimePluginOptions<AhkConfig> _brokerConfig;
    private readonly ILogger<AhkFeedWorker> _logger;
    private readonly TradingActivityLog? _activity;

    /// <summary>Last <see cref="AhkPortalClient.SessionEpoch"/> this worker has subscribed against.</summary>
    private int _seenSessionEpoch;

    /// <summary>
    /// Symbols the most recent non-empty poll actually carried. Kept for one reason: when the silence
    /// watchdog fires, "the feed returned nothing" is the symptom and this is the evidence. Without it the
    /// only recoverable fact is that a re-subscribe happened, which is what made the same message repeat
    /// for an hour on 2026-08-19 while telling nobody whether the subscription was the problem.
    /// </summary>
    private IReadOnlyList<string> _lastFeedSymbols = [];
    private DateTime _lastSubscribeUtc = DateTime.MinValue;
    private DateOnly _bookSession;
    private IReadOnlyList<string> _subscribed = [];
    private bool _resubscribePending;
    private string? _resubscribeReason;
    private int _consecutiveFailures;
    private DateTime? _lastSeenOpenPkt;

    /// <summary>Decides when a lost subscription has to be re-sent. See <see cref="FeedSubscriptionGuard"/>.</summary>
    private readonly FeedSubscriptionGuard _subscriptionGuard = new();

    /// <summary>
    /// Earliest time another session-establishment attempt may run. Establishing a session launches a
    /// browser and performs a real login, so failures back off instead of repeating on the quote
    /// cadence — see the backoff branch in the poll loop.
    /// </summary>
    private DateTime _sessionRetryNotBeforeUtc = DateTime.MinValue;

    /// <summary>Consecutive failed session-establishment attempts, used to grow the backoff.</summary>
    private int _sessionFailures;

    public AhkFeedWorker(
        AhkPortalClient portal,
        AhkQuoteBook book,
        AhkDepthBook depth,
        AhkBroker broker,
        MonitoredUniverse universe,
        IMarketCalendar calendar,
        IRuntimePluginOptions<AhkFeedConfig> config,
        IRuntimePluginOptions<AhkConfig> brokerConfig,
        ILogger<AhkFeedWorker> logger,
        TradingActivityLog? activity = null)
    {
        _activity = activity;
        _portal = portal;
        _book = book;
        _depth = depth;
        _broker = broker;
        _universe = universe;
        _calendar = calendar;
        _config = config;
        _brokerConfig = brokerConfig;
        _logger = logger;
    }

    /// <summary>True when the book is being kept current and may be trusted as a quote source.</summary>
    public bool IsHealthy =>
        _config.Current.Enabled &&
        _consecutiveFailures < Math.Max(1, _config.Current.UnhealthyAfterConsecutiveFailures);

    /// <summary>Symbols currently subscribed on the feed, for status reporting.</summary>
    public IReadOnlyList<string> SubscribedSymbols => _subscribed;

    /// <summary>Latest market status the feed reported ("OPEN", "CLOSED", "OHO", …).</summary>
    public string? MarketStatus => _portal.LastMarketStatus;

    /// <summary>
    /// A point-in-time view of the feed for operators.
    ///
    /// <para>
    /// This exists because every failure mode of this worker is SILENT. A lost subscription, a dead
    /// session, a browser holding the screen and an genuinely quiet market all present as "no
    /// quotes", and none of them raises anything. Without a surface like this the only way to tell
    /// them apart is to read the log at Debug level, which nobody does until something has already
    /// gone wrong.
    /// </para>
    /// </summary>
    public AhkFeedStatus GetStatus()
    {
        var cfg = _config.Current;
        var maxAge = TimeSpan.FromSeconds(Math.Max(30.0, cfg.MaxQuoteAgeSeconds));
        var now = DateTime.UtcNow;

        return new AhkFeedStatus
        {
            Enabled            = cfg.Enabled,
            Healthy            = IsHealthy,
            PortalMarketStatus = _portal.LastMarketStatus,
            // AccountCode is retained for diagnostics after a session expires. It is not proof that
            // the direct API is still authenticated; reporting it as one made feed status disagree
            // with reconciliation about the same AhkPortalClient instance.
            SessionEstablished = _portal.HasSession,
            Account            = _portal.AccountCode,
            SubscribedSymbols  = _subscribed.Count,
            BookSymbols        = _book.Count,
            FreshSymbols       = _book.Snapshot(
                                     string.IsNullOrWhiteSpace(cfg.Market) ? "REG" : cfg.Market,
                                     maxAge, now).Count,
            LastUpdateUtc      = _book.LastUpdateUtc,
            SecondsSinceUpdate = _book.LastUpdateUtc is { } last
                                     ? Math.Round((now - last).TotalSeconds, 1)
                                     : null,
            SilentPolls        = _subscriptionGuard.SilentPolls,
            LastFeedSymbols    = _lastFeedSymbols.Count,
            ConsecutiveFailures = _consecutiveFailures,
            SessionFailures    = _sessionFailures,
            AutomaticSessionRecovery = _portal.AutomaticRecoveryArmed,
            FreshLoginRequired = _portal.FreshLoginRequired,
            LoginFailures      = _portal.ConsecutiveLoginFailures,
            LastLoginAttemptUtc = _portal.LastLoginAttemptUtc,
            LastLoginSuccessUtc = _portal.LastLoginSuccessUtc,
            NextLoginAttemptUtc = _portal.NextLoginAttemptUtc,
            LastKeepAliveSuccessUtc = _portal.LastKeepAliveSuccessUtc,
            BrowserHoldsScreen = _broker.BrowserHoldsTradingScreen,
            PollSeconds        = Math.Max(2.0, cfg.PollSeconds)
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.Current.Enabled)
        {
            _logger.LogInformation(
                "[AhkFeed] Disabled (Plugins:AhkFeed:Enabled = false); prices come from the PSX market watch.");
            return;
        }

        // Credentials are checked BEFORE anything else, because the feed is now enabled by default.
        // Without them every pass would launch Chromium, fail the positional-password login and back
        // off — up to six browser launches an hour, against a broker, for a deployment that simply has
        // not been configured yet. Refusing to start is both cheaper and clearer than retrying.
        var broker = _brokerConfig.Current;
        if (string.IsNullOrWhiteSpace(broker.Username) || string.IsNullOrWhiteSpace(broker.Password))
        {
            _logger.LogInformation(
                "[AhkFeed] No broker credentials configured (Plugins:Ahk:Username/Password), so the "
                + "live quote feed will not start. Prices come from the PSX market watch.");
            return;
        }

        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        _logger.LogInformation("[AhkFeed] Starting live quote feed against the broker portal.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var cfg = _config.Current;
            var delay = TimeSpan.FromSeconds(Math.Max(2.0, cfg.PollSeconds));

            try
            {
                // Re-read the switch every pass so it can be turned off at runtime from the web UI
                // without a restart — this loop holds a broker session, which an operator may need
                // to reclaim in a hurry.
                if (!cfg.Enabled)
                {
                    delay = IdlePoll;
                }
                else if (!ShouldPollNow(cfg))
                {
                    // Outside market hours the feed publishes nothing, so polling it would be load on
                    // the broker for no data — and would hold a session open around the clock.
                    delay = IdlePoll;
                    ResetBookOnNewSession();
                }
                else if (_broker.BrowserHoldsTradingScreen)
                {
                    // The browser's own site.js is polling GetFeed right now. Yield rather than split
                    // a possibly drain-once queue with it.
                    _subscriptionGuard.NoteBrowserHoldsScreen();
                    _logger.LogDebug("[AhkFeed] Browser holds the trading screen; skipping this poll.");
                }
                else if (!_portal.HasSession && DateTime.UtcNow < _sessionRetryNotBeforeUtc)
                {
                    // Backing off after a failed login. Establishing a session LAUNCHES A BROWSER, so
                    // retrying this on the 2s quote cadence is a Chromium relaunch every two seconds
                    // and a login attempt against the broker every two seconds — which is both a
                    // local resource fire and the kind of traffic that gets an account locked out.
                    // Quote polling is worthless without a session anyway, so there is nothing to
                    // lose by waiting.
                }
                else
                {
                    // The browser has just let go of the trading screen. Its site.js re-subscribes
                    // Page1 from its OWN market-watch table on every load, and that table is almost
                    // always empty (the portal does not persist it), so opening the trading screen
                    // REPLACES our server-side subscription with an empty one. Nothing reports this:
                    // GetFeed simply starts returning [], which is indistinguishable from a market
                    // where nothing is trading. Forcing a re-subscribe on release is what stops an
                    // order placement from silently killing the price feed behind it.
                    if (_subscriptionGuard.NoteBrowserReleasedScreen())
                    {
                        ForceResubscribe("the browser released the trading screen and its page load "
                                       + "will have overwritten the subscription");
                    }

                    await PollOnceAsync(cfg, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A quote feed must never take the host down; the composite falls back to PSX.
                _consecutiveFailures++;
                _logger.LogWarning(ex, "[AhkFeed] Poll failed ({Count} consecutive).", _consecutiveFailures);
                delay = IdlePoll;
            }

            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("[AhkFeed] Stopped.");
    }

    private async Task PollOnceAsync(AhkFeedConfig cfg, CancellationToken ct)
    {
        ResetBookOnNewSession();

        if (!await _portal.EnsureSessionAsync(ct))
        {
            _consecutiveFailures++;

            // 30s, 60s, 120s … capped at 10 minutes. A failed session is almost always a
            // configuration problem (missing or rejected credentials) rather than a blip, and those
            // do not resolve by trying again immediately — they resolve when a human fixes the
            // config, which the log line above is telling them to do.
            _sessionFailures++;
            var backoff = TimeSpan.FromSeconds(Math.Min(600, 30 * Math.Pow(2, Math.Min(5, _sessionFailures - 1))));
            _sessionRetryNotBeforeUtc = DateTime.UtcNow + backoff;

            _logger.LogWarning(
                "[AhkFeed] Could not establish a broker session (attempt {Count}); next attempt in {Backoff}. "
                + "Quotes fall back to the PSX market watch until then.",
                _sessionFailures, backoff);
            _activity?.Warn("Feed",
                $"Could not establish a broker session for the live feed (attempt {_sessionFailures})",
                $"Retrying in {backoff.TotalSeconds:F0}s. Quotes come from the PSX market watch until then.");
            return;
        }

        if (_sessionFailures > 0)
            _activity?.Info("Feed", "Live quote feed session established");
        _sessionFailures = 0;

        // Subscriptions live on the SESSION, so a re-established one starts with none — while
        // _subscribed still names the symbols the previous session was carrying. Believing that cache
        // is a subscription costs a full watchdog window (thirty quiet polls of an open market) before
        // anything recovers, because an unsubscribed GetFeed and a market where nothing trades are the
        // same empty 200. Comparing the epoch turns that into a re-subscribe on the very next pass.
        if (_portal.SessionEpoch != _seenSessionEpoch)
        {
            var previous = _seenSessionEpoch;
            _seenSessionEpoch = _portal.SessionEpoch;

            // Not on the FIRST session: there is no subscription to have lost yet, and ForceResubscribe
            // would only log a confusing recovery for an ordinary startup.
            if (previous != 0)
                ForceResubscribe("the portal session was re-established, and subscriptions do not survive it");
        }

        await EnsureSubscriptionAsync(cfg, ct);

        var response = await _portal.GetFeedAsync(ct);
        if (response is null)
        {
            _consecutiveFailures++;
            return;
        }

        _consecutiveFailures = 0;

        var returned = (response.Feed ?? [])
            .Select(q => q.Symbol?.Trim().ToUpperInvariant())
            .Where(sym => !string.IsNullOrEmpty(sym))
            .Select(sym => sym!)
            .Distinct()
            .ToList();
        if (returned.Count > 0) _lastFeedSymbols = returned;

        var applied = _book.Apply(response.Feed ?? [], DateTime.UtcNow);

        // Depth rides along on the same response, so ingesting it costs nothing per poll. It is only
        // ever non-empty while a depth subscription is active.
        if (response.MbpFeed is { Count: > 0 } || response.MboFeed is { Count: > 0 })
        {
            var cfgNow = _config.Current;
            var mkt = string.IsNullOrWhiteSpace(cfgNow.Market) ? "REG" : cfgNow.Market.Trim().ToUpperInvariant();
            _depth.Ingest(response.MbpFeed, response.MboFeed, mkt, _depth.SubscribedSymbol);
        }
        if (applied > 0)
        {
            _logger.LogDebug("[AhkFeed] Applied {Applied} quote update(s); book holds {Count} symbol(s).",
                applied, _book.Count);
        }

        if (_subscriptionGuard.NotePollResult(
                appliedAnyQuotes: applied > 0,
                portalMarketOpen: IsPortalMarketOpen(),
                hasSubscription: _subscribed.Count > 0,
                silentPollThreshold: cfg.ResubscribeAfterSilentPolls))
        {
            // The diff, not just the fact. A subscription of 30 symbols that returns 30 and then stops is a
            // different failure from one that never returned a given symbol at all, and the two need
            // different fixes — one is the session, the other is the watchlist.
            var missing = _subscribed
                .Where(sym => !_lastFeedSymbols.Contains(sym, StringComparer.OrdinalIgnoreCase))
                .ToList();

            ForceResubscribe(
                $"the feed returned nothing for {Math.Max(5, cfg.ResubscribeAfterSilentPolls)} " +
                $"consecutive polls while the market is open (subscribed {_subscribed.Count}; " +
                $"last non-empty poll carried {_lastFeedSymbols.Count}" +
                (missing.Count == 0
                    ? ", all of them"
                    : $"; never seen: {string.Join(", ", missing.Take(8))}" +
                      (missing.Count > 8 ? $" and {missing.Count - 8} more" : "")) + ")");
        }
    }

    /// <summary>
    /// True when the portal itself says the market is open. Deliberately the PORTAL's opinion rather
    /// than the local calendar: the watchdog is asking "should this feed be producing data right
    /// now", and the feed's own view of the session is the authority on that.
    /// </summary>
    private bool IsPortalMarketOpen() =>
        string.Equals(_portal.LastMarketStatus, "OPEN", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Clears the remembered subscription so the next pass re-sends it. Does NOT clear the quote
    /// book: the prices already collected are still the best available, and dropping them would
    /// turn a recoverable subscription glitch into a gap in the data.
    /// </summary>
    private void ForceResubscribe(string reason)
    {
        if (_subscribed.Count == 0 && _lastSubscribeUtc == DateTime.MinValue) return;

        _logger.LogInformation("[AhkFeed] Re-subscribing because {Reason}.", reason);
        _activity?.Info("Feed", "Re-subscribing the live quote feed", reason);
        _resubscribePending = true;
        _resubscribeReason = reason;
        _subscribed = [];
        _lastSubscribeUtc = DateTime.MinValue;
    }

    /// <summary>
    /// Pushes the monitored universe onto the portal's subscription slots, re-sending when the
    /// universe changes. Re-subscribing is also how the feed recovers after a session replacement:
    /// subscriptions live on the session, so a new session starts with none.
    /// </summary>
    private async Task EnsureSubscriptionAsync(AhkFeedConfig cfg, CancellationToken ct)
    {
        var symbols = (await _universe.ForMonitoringAsync(ct))
            .Select(s => s.Trim().ToUpperInvariant())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var pageSize = Math.Clamp(cfg.SymbolsPerPage, 1, 200);
        var pages = FeedPagePlanner.NormalizePages(cfg.Pages);

        var (assignments, dropped) = FeedPagePlanner.Plan(symbols, pageSize, pages);
        if (dropped.Count > 0)
        {
            // Truncating silently would look identical to "those symbols never trade" — the missing
            // ones would just never appear in a snapshot. Name them, and let PSX cover them.
            _logger.LogWarning(
                "[AhkFeed] {Total} symbols exceed the feed capacity of {Capacity} ({Pages} pages × {PageSize}); " +
                "{Dropped} will be served from the PSX market watch instead: {Symbols}",
                symbols.Count, pageSize * pages.Count, pages.Count, pageSize,
                dropped.Count, string.Join(", ", dropped));
            symbols = symbols.Take(pageSize * pages.Count).ToList();
        }

        // Nothing to do when the set is unchanged; re-subscribing every poll would be pointless load.
        var unchanged = _subscribed.SequenceEqual(symbols, StringComparer.OrdinalIgnoreCase);
        if (unchanged && DateTime.UtcNow - _lastSubscribeUtc < TimeSpan.FromMinutes(10)) return;

        var market = string.IsNullOrWhiteSpace(cfg.Market) ? "REG" : cfg.Market.Trim().ToUpperInvariant();

        foreach (var (page, pageSymbols) in assignments)
        {
            var slice = pageSymbols.Select(s => new AhkSymbolKey(market, s)).ToList();

            // An emptied page still gets sent: the slot holds whatever was last put in it, so
            // shrinking the universe without clearing the tail would keep streaming dropped symbols.
            if (!await _portal.SubscribeAsync(page, slice, "MKT-FEED", ct))
            {
                _logger.LogWarning("[AhkFeed] Subscription for {Page} failed; retrying next pass.", page);
                if (_resubscribePending)
                {
                    _activity?.Warn("Feed", "Live quote feed re-subscription failed",
                        $"{page} was refused; retrying next pass. Trigger: {_resubscribeReason}");
                }
                return;
            }
        }

        _subscribed = symbols;
        _lastSubscribeUtc = DateTime.UtcNow;

        if (_resubscribePending)
        {
            _activity?.Info("Feed", "Live quote feed re-subscribed",
                $"{symbols.Count} symbol(s) across {pages.Count} page(s). Trigger: {_resubscribeReason}");
            _resubscribePending = false;
            _resubscribeReason = null;
        }

        // Evict anything the portal will no longer send. A symbol dropped from the watchlist stops
        // arriving but its last quote would otherwise stay servable for the whole freshness window.
        var evicted = _book.RetainOnly(market, symbols);

        if (!unchanged)
        {
            _logger.LogInformation(
                "[AhkFeed] Subscribed {Count} symbol(s) across {Pages} page(s){Evicted}.",
                symbols.Count, pages.Count,
                evicted > 0 ? $"; evicted {evicted} unwatched symbol(s) from the book" : "");
        }
    }

    /// <summary>
    /// True when the market is open, or within the configured window either side of it. Honours the
    /// same calendar the rest of the plugin uses, so the feed and the monitor agree on what "open" means.
    ///
    /// <para>
    /// The pre-open side comes from the calendar's next-open time. The post-close side is tracked
    /// here instead, because <see cref="MarketStatus"/> publishes no close time — remembering when
    /// the session was last seen open is enough, and it avoids widening the calendar's contract for
    /// one caller.
    /// </para>
    /// </summary>
    private bool ShouldPollNow(AhkFeedConfig cfg)
    {
        var status = _calendar.GetStatus();
        if (status.IsOpen)
        {
            _lastSeenOpenPkt = status.PktNow;
            return true;
        }

        if (!cfg.OnlyDuringMarketHours) return true;

        var margin = TimeSpan.FromMinutes(Math.Max(0, cfg.PrePostMarketMinutes));
        if (margin <= TimeSpan.Zero) return false;

        var now = status.PktNow;

        // Pre-open: the auction carries real price formation worth capturing.
        if (status.NextOpenPkt is { } open && open >= now && open - now <= margin) return true;

        // Post-close: the final prints settle for a few minutes after the bell.
        return _lastSeenOpenPkt is { } lastOpen && now >= lastOpen && now - lastOpen <= margin;
    }

    /// <summary>
    /// Clears the book when the trading date rolls over, so yesterday's last prices cannot be served
    /// as today's before the first tick arrives. Without this the first monitoring pass of the
    /// morning would evaluate levels against stale closes that look perfectly fresh.
    /// </summary>
    private void ResetBookOnNewSession()
    {
        var today = PsxTime.Today();
        if (_bookSession == today) return;

        if (_book.Count > 0)
            _logger.LogInformation("[AhkFeed] New trading session {Date}; clearing the quote book.", today);

        _book.Clear();
        _bookSession = today;
        _subscribed = [];
        _lastSubscribeUtc = DateTime.MinValue;
        _subscriptionGuard.Reset();
    }
    // ── Market depth ──────────────────────────────────────────────────────────

    /// <summary>
    /// Points the depth subscription at one symbol, replacing whatever it was following.
    ///
    /// <para>
    /// One symbol at a time, matching the portal: its own UI has a single MBO panel and a single MBP
    /// panel. Passing null clears the focus but leaves the slot as the portal last set it — there is no
    /// unsubscribe verb, and re-subscribing an empty page for a depth feed was never tested, so the
    /// honest behaviour is to stop reading rather than to send an untested call.
    /// </para>
    ///
    /// <para>
    /// Refuses when the configured depth page overlaps a quote-feed page, because a subscription
    /// replaces a slot for every feed type and the portal reports nothing when a page is emptied — the
    /// only symptom would be quotes silently stopping for those symbols.
    /// </para>
    /// </summary>
    /// <returns>Null on success, or the reason it was refused.</returns>
    public async Task<string?> FocusDepthAsync(string? symbol, CancellationToken ct = default)
    {
        var cfg = _config.Current;
        if (!cfg.Enabled) return "The broker feed is disabled, so depth cannot be subscribed.";
        if (!cfg.DepthEnabled) return "Market depth is disabled (AhkFeed:DepthEnabled).";

        if (symbol is null)
        {
            _depth.SubscribedSymbol = null;
            return null;
        }

        symbol = symbol.Trim().ToUpperInvariant();

        var page = cfg.DepthPage?.Trim();
        if (string.IsNullOrEmpty(page)) return "AhkFeed:DepthPage is not configured.";

        var quotePages = FeedPagePlanner.NormalizePages(cfg.Pages);
        if (quotePages.Contains(page, StringComparer.OrdinalIgnoreCase))
        {
            // Fail closed. Overlapping would evict that page's quote symbols with no error anywhere.
            return $"DepthPage '{page}' is also a quote-feed page ({string.Join(", ", quotePages)}). " +
                   "Subscribing depth there would silently stop quotes for those symbols. " +
                   "Use a page outside the quote set, or shrink AhkFeed:Pages.";
        }

        if (!_portal.HasSession)
            return "No broker session is available; depth needs a live session.";

        var market = string.IsNullOrWhiteSpace(cfg.Market) ? "REG" : cfg.Market.Trim().ToUpperInvariant();
        if (!await _portal.SubscribeDepthAsync(page, market, symbol, ct))
            return $"The portal refused the depth subscription for {symbol}.";

        _depth.SubscribedSymbol = symbol;
        _activity?.Info("Feed", $"Market depth following {symbol}",
            $"MBP and MBO on {page}; rows arrive on the existing feed poll.");
        return null;
    }

}

/// <summary>Operator-facing snapshot of <see cref="AhkFeedWorker"/>; see its GetStatus remarks.</summary>
public sealed record AhkFeedStatus
{
    public bool Enabled { get; init; }
    public bool Healthy { get; init; }

    /// <summary>The portal's own view of the session: OPEN, CLOSED, OHO, or null if never polled.</summary>
    public string? PortalMarketStatus { get; init; }

    public bool SessionEstablished { get; init; }
    public string? Account { get; init; }

    /// <summary>Symbols currently subscribed on the portal.</summary>
    public int SubscribedSymbols { get; init; }

    /// <summary>
    /// Symbols the last non-empty poll carried. Compared against <see cref="SubscribedSymbols"/> this is
    /// the fastest read on whether a quiet feed is unsubscribed or simply quiet.
    /// </summary>
    public int LastFeedSymbols { get; init; }

    /// <summary>Symbols the book holds at all, regardless of age.</summary>
    public int BookSymbols { get; init; }

    /// <summary>Symbols young enough to actually be served. A gap from BookSymbols means staleness.</summary>
    public int FreshSymbols { get; init; }

    public DateTime? LastUpdateUtc { get; init; }
    public double? SecondsSinceUpdate { get; init; }

    /// <summary>Consecutive empty polls during an open market — the lost-subscription signal.</summary>
    public int SilentPolls { get; init; }

    public int ConsecutiveFailures { get; init; }
    public int SessionFailures { get; init; }
    public bool AutomaticSessionRecovery { get; init; }
    public bool FreshLoginRequired { get; init; }
    public int LoginFailures { get; init; }
    public DateTime? LastLoginAttemptUtc { get; init; }
    public DateTime? LastLoginSuccessUtc { get; init; }
    public DateTime? NextLoginAttemptUtc { get; init; }
    public DateTime? LastKeepAliveSuccessUtc { get; init; }

    /// <summary>True while the feed is yielding to a browser operation on the trading screen.</summary>
    public bool BrowserHoldsScreen { get; init; }

    public double PollSeconds { get; init; }
}
