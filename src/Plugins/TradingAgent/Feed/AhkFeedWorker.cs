using AgentFox.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingAgent.Broker;
using TradingAgent.Config;
using TradingAgent.Market;
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
    private readonly AhkBroker _broker;
    private readonly MonitoredUniverse _universe;
    private readonly IMarketCalendar _calendar;
    private readonly IRuntimePluginOptions<AhkFeedConfig> _config;
    private readonly ILogger<AhkFeedWorker> _logger;

    private DateTime _lastReloginUtc = DateTime.MinValue;
    private DateTime _lastSubscribeUtc = DateTime.MinValue;
    private DateOnly _bookSession;
    private IReadOnlyList<string> _subscribed = [];
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
        AhkBroker broker,
        MonitoredUniverse universe,
        IMarketCalendar calendar,
        IRuntimePluginOptions<AhkFeedConfig> config,
        ILogger<AhkFeedWorker> logger)
    {
        _portal = portal;
        _book = book;
        _broker = broker;
        _universe = universe;
        _calendar = calendar;
        _config = config;
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
            SessionEstablished = _sessionFailures == 0 && _portal.AccountCode is not null,
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
            ConsecutiveFailures = _consecutiveFailures,
            SessionFailures    = _sessionFailures,
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
                else if (DateTime.UtcNow < _sessionRetryNotBeforeUtc)
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
            return;
        }

        _sessionFailures = 0;

        // Keep the session alive. The portal's UI does this about once a minute; letting it lapse
        // turns every subsequent call into a redirect to the login page.
        if (DateTime.UtcNow - _lastReloginUtc > TimeSpan.FromSeconds(Math.Max(15, cfg.ReloginSeconds)))
        {
            _lastReloginUtc = DateTime.UtcNow;
            if (!await _portal.ReloginAsync(ct))
            {
                _consecutiveFailures++;
                return;
            }
        }

        await EnsureSubscriptionAsync(cfg, ct);

        var response = await _portal.GetFeedAsync(ct);
        if (response is null)
        {
            _consecutiveFailures++;
            return;
        }

        _consecutiveFailures = 0;

        var applied = _book.Apply(response.Feed ?? [], DateTime.UtcNow);
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
            ForceResubscribe(
                $"the feed returned nothing for {Math.Max(5, cfg.ResubscribeAfterSilentPolls)} " +
                "consecutive polls while the market is open");
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
                return;
            }
        }

        _subscribed = symbols;
        _lastSubscribeUtc = DateTime.UtcNow;

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

    /// <summary>True while the feed is yielding to a browser operation on the trading screen.</summary>
    public bool BrowserHoldsScreen { get; init; }

    public double PollSeconds { get; init; }
}
