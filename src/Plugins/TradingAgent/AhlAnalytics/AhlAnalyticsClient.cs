using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentFox.Plugins;
using Microsoft.Extensions.Logging;
using TradingAgent.Config;
using TradingAgent.Feed;

namespace TradingAgent.AhlAnalytics;

/// <summary>
/// Talks to the AHL Analytics research portal (<c>data.arifhabibltd.com</c>) over plain HTTP.
/// See <c>docs/ahl-analytics-api.md</c> for the captured protocol.
///
/// <para>
/// <b>The handshake, and why it needs no browser.</b> Three hops: the broker portal mints a
/// pre-signed SSO URL (<c>AhkPortalClient.GetAnalyticsUrlAsync</c>), GETting that URL returns
/// server-rendered HTML whose <c>&lt;meta name="access-token"&gt;</c> carries a Bearer JWT, and every
/// <c>/api/**</c> call then presents that JWT. The token is in a meta tag rather than set by script,
/// so a regex over the response body replaces a headless browser entirely. Only hop ① needs the
/// broker session; hops ② and ③ are anonymous HTTP against a token.
/// </para>
///
/// <para>
/// <b>The token is long-lived, which changes the caching strategy.</b> It is a Laravel Passport
/// personal access token with an <c>exp</c> roughly a year out, and the SSO blob that mints it is
/// replayable — refetching returns the SAME token. So this is not a session to be kept warm; it is a
/// credential to be cached and reused. Re-handshaking per call would burn a broker login for nothing.
/// </para>
///
/// <para>
/// <b>The 401 trap.</b> The portal punishes a burst with <c>401 {"message":"Unauthenticated."}</c>,
/// not 429, while the token remains perfectly valid. A client that treated 401 as "token dead" would
/// re-run the handshake on every throttle, and because hop ① can launch Chromium to restore a dead
/// broker session, that means a LOGIN per throttle against a broker that has withdrawn access before.
/// So: a client-side rate limiter keeps us off the cliff, and a 401 backs off and RETRIES on the same
/// token first, only re-handshaking if the retry also fails.
/// </para>
///
/// <para>
/// Everything is fail-soft — null or empty rather than throwing. Callers are agent tools and a
/// dashboard; both must report a problem rather than propagate one.
/// </para>
/// </summary>
public sealed class AhlAnalyticsClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    /// <summary>
    /// Pulls the Bearer token out of the SSO landing page. Anchored on the meta tag the portal
    /// renders server-side; only the first 8 KB of the document is worth scanning since it lives in
    /// <c>&lt;head&gt;</c>.
    /// </summary>
    private static readonly Regex AccessTokenMeta = new(
        """<meta\s+name=["']access-token["']\s+content=["']([^"']+)["']""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly AhkPortalClient _portal;
    private readonly IRuntimePluginOptions<AhlAnalyticsConfig> _config;
    private readonly ILogger<AhlAnalyticsClient> _logger;

    /// <summary>Serialises the handshake so a burst of cold callers performs one, not five.</summary>
    private readonly SemaphoreSlim _authGate = new(1, 1);
    /// <summary>Serialises snapshot fetches so a burst does not pull 1.1 MB per caller.</summary>
    private readonly SemaphoreSlim _snapshotGate = new(1, 1);

    private readonly HttpClient _http;

    private string? _bearer;
    private DateTimeOffset _bearerObtainedAt;

    private AhlSnapshotData? _snapshot;
    private DateTimeOffset _snapshotAt;

    /// <summary>Timestamps of recent requests, for the client-side rate limiter.</summary>
    private readonly Queue<DateTimeOffset> _recent = new();
    private readonly object _rateLock = new();

    public AhlAnalyticsClient(
        AhkPortalClient portal,
        IRuntimePluginOptions<AhlAnalyticsConfig> config,
        ILogger<AhlAnalyticsClient> logger)
    {
        _portal = portal;
        _config = config;
        _logger = logger;

        _http = new HttpClient(new SocketsHttpHandler
        {
            // Same corporate-proxy reason as AhkPortalClient: without this, every call on a network
            // with an authenticating proxy dies at the tunnel with 407 before reaching the portal,
            // and the symptom is misleading — the handshake's broker hop succeeds (it uses a
            // different client), so it looks like the analytics portal is down rather than the proxy
            // saying no. Null credentials are harmless where no proxy exists.
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,
            // The SSO hop is an http→https 307 that MUST be followed, and the API hops answer 401
            // rather than redirecting, so unlike the broker portal there is no expiry-detection
            // reason to see redirects.
            AllowAutoRedirect = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        _http.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        _http.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
        _http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/151.0.0.0 Safari/537.36");
    }

    private AhlAnalyticsConfig Config => _config.Current;

    /// <summary>Whether the plugin is configured to use the analytics portal at all.</summary>
    public bool Enabled => Config.Enabled;

    /// <summary>Whether a usable Bearer token is already held, WITHOUT performing a handshake.</summary>
    public bool HasToken =>
        _bearer is not null &&
        DateTimeOffset.UtcNow - _bearerObtainedAt < TimeSpan.FromHours(Math.Max(1, Config.TokenLifetimeHours));

    private Uri BaseUri => new(Config.BaseUrl.TrimEnd('/') + "/");

    // ── authentication ────────────────────────────────────────────────────────

    /// <summary>
    /// Ensures a Bearer token is held, performing the SSO handshake if not.
    /// </summary>
    /// <param name="force">Discard any held token first — used by the 401 path.</param>
    private async Task<string?> EnsureTokenAsync(bool force, CancellationToken ct)
    {
        if (!Enabled) return null;
        if (!force && HasToken) return _bearer;

        await _authGate.WaitAsync(ct);
        try
        {
            // Another caller may have completed the handshake while we waited on the gate.
            if (!force && HasToken) return _bearer;

            // Hop ①: the broker portal mints the SSO URL. This is the only hop needing the broker
            // session, and the only one that can cost a login.
            var ssoUrl = await _portal.GetAnalyticsUrlAsync(ct);
            if (ssoUrl is null)
            {
                _logger.LogWarning("[AhlAnalytics] Could not obtain the analytics SSO URL " +
                                   "(no broker session, or the portal refused).");
                return null;
            }

            // Hop ②: follow it. The response is HTML; the token is a meta tag in <head>.
            string html;
            try
            {
                using var response = await _http.GetAsync(ssoUrl, ct);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("[AhlAnalytics] SSO landing page answered {Status}.",
                        (int)response.StatusCode);
                    return null;
                }
                html = await response.Content.ReadAsStringAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AhlAnalytics] SSO landing page request failed.");
                return null;
            }

            var match = AccessTokenMeta.Match(html.Length > 16_384 ? html[..16_384] : html);
            if (!match.Success)
            {
                _logger.LogWarning(
                    "[AhlAnalytics] SSO landing page carried no access-token meta tag " +
                    "({Length} bytes). The portal's login markup may have changed.", html.Length);
                return null;
            }

            _bearer = match.Groups[1].Value;
            _bearerObtainedAt = DateTimeOffset.UtcNow;
            _logger.LogInformation("[AhlAnalytics] Obtained an analytics API token via SSO.");
            return _bearer;
        }
        finally
        {
            _authGate.Release();
        }
    }

    // ── transport ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Blocks until sending one more request keeps us under the configured per-minute ceiling.
    /// Cheap and approximate on purpose — the point is to stay off the portal's 401 cliff, not to
    /// meter precisely.
    /// </summary>
    private async Task ThrottleAsync(CancellationToken ct)
    {
        var limit = Math.Max(1, Config.MaxRequestsPerMinute);

        while (true)
        {
            TimeSpan wait;
            lock (_rateLock)
            {
                var now = DateTimeOffset.UtcNow;
                while (_recent.Count > 0 && now - _recent.Peek() > TimeSpan.FromMinutes(1))
                    _recent.Dequeue();

                if (_recent.Count < limit)
                {
                    _recent.Enqueue(now);
                    return;
                }

                wait = TimeSpan.FromMinutes(1) - (now - _recent.Peek()) + TimeSpan.FromMilliseconds(50);
            }

            _logger.LogDebug("[AhlAnalytics] Rate limiter holding for {Ms}ms.", (int)wait.TotalMilliseconds);
            await Task.Delay(wait, ct);
        }
    }

    /// <summary>
    /// Sends one API request, handling the throttle-as-401 behaviour described on the class.
    /// </summary>
    private async Task<string?> SendAsync(
        HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        if (!Enabled) return null;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var token = await EnsureTokenAsync(force: attempt == 2, ct);
            if (token is null) return null;

            await ThrottleAsync(ct);

            using var request = new HttpRequestMessage(method, new Uri(BaseUri, path.TrimStart('/')));
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            if (content is not null) request.Content = content;

            try
            {
                using var response = await _http.SendAsync(request, ct);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    // Ambiguous by design on this portal: a real auth failure and a rate-limit
                    // rejection are the same status. Back off and retry on the SAME token first; only
                    // the last attempt re-handshakes, so a throttle cannot cost a broker login.
                    var backoff = TimeSpan.FromSeconds(2 * (attempt + 1));
                    _logger.LogWarning(
                        "[AhlAnalytics] {Path} answered 401 (attempt {Attempt}); backing off {Sec}s. " +
                        "On this portal a 401 usually means rate-limited, not unauthenticated.",
                        path, attempt + 1, backoff.TotalSeconds);
                    await Task.Delay(backoff, ct);
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    // A real permission boundary — e.g. /analyst-opinion/target, which this account
                    // is not entitled to. Retrying cannot help, so do not.
                    _logger.LogInformation(
                        "[AhlAnalytics] {Path} is not permitted for this account (403).", path);
                    return null;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("[AhlAnalytics] {Method} {Path} returned {Status}.",
                        method, path, (int)response.StatusCode);
                    return null;
                }

                return await response.Content.ReadAsStringAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AhlAnalytics] {Method} {Path} failed.", method, path);
                return null;
            }
        }

        _logger.LogWarning("[AhlAnalytics] {Path} still failing after retries.", path);
        return null;
    }

    private async Task<T?> GetJsonAsync<T>(string path, CancellationToken ct) where T : class
    {
        var body = await SendAsync(HttpMethod.Get, path, null, ct);
        return Deserialize<T>(body, path);
    }

    private async Task<T?> PostJsonAsync<T>(
        string path, HttpContent? content, CancellationToken ct) where T : class
    {
        var body = await SendAsync(HttpMethod.Post, path, content, ct);
        return Deserialize<T>(body, path);
    }

    private T? Deserialize<T>(string? body, string endpoint) where T : class
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(body, Json);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("[AhlAnalytics] {Endpoint} returned unparseable JSON: {Message}",
                endpoint, ex.Message);
            return null;
        }
    }

    // ── whole-market snapshot ─────────────────────────────────────────────────

    /// <summary>
    /// The whole-market snapshot, cached for <see cref="AhlAnalyticsConfig.SnapshotCacheSeconds"/>.
    ///
    /// <para>
    /// This one call is what makes the movers screens and a market-wide scan cheap: 857 equities with
    /// prices, technicals, float, circuit caps and fundamentals, for one request. Everything derived
    /// from it — gainers, unusual volume, sector rotation — is a sort over this object, not more I/O.
    /// </para>
    /// </summary>
    public async Task<AhlSnapshotData?> GetMarketSnapshotAsync(
        bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!Enabled) return null;

        var ttl = TimeSpan.FromSeconds(Math.Max(5, Config.SnapshotCacheSeconds));
        if (!forceRefresh && _snapshot is not null && DateTimeOffset.UtcNow - _snapshotAt < ttl)
            return _snapshot;

        await _snapshotGate.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _snapshot is not null && DateTimeOffset.UtcNow - _snapshotAt < ttl)
                return _snapshot;

            // The endpoint is a POST with a form body; `item=market` is the only value the portal's
            // own UI ever sends.
            using var form = new FormUrlEncodedContent(
                new Dictionary<string, string> { ["item"] = "market" });

            var response = await PostJsonAsync<AhlMarketSnapshot>(
                "api/v3/market?path=/req", form, ct);

            if (response?.Data?.Equities is null or { Count: 0 })
            {
                _logger.LogWarning("[AhlAnalytics] Market snapshot came back without equities.");
                return _snapshot; // keep serving the previous one rather than dropping to nothing
            }

            _snapshot = response.Data;
            _snapshotAt = DateTimeOffset.UtcNow;
            _logger.LogDebug("[AhlAnalytics] Snapshot refreshed: {Equities} equities, state {State}.",
                _snapshot.Equities!.Count, _snapshot.MarketState);
            return _snapshot;
        }
        finally
        {
            _snapshotGate.Release();
        }
    }

    // ── candles ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Daily candles — about 1235 bars (five years) in one call, <b>oldest first</b> after the
    /// reversal this method performs.
    ///
    /// <para>
    /// <b>These closes are corporate-action adjusted</b> and the portal ignores any parameter asking
    /// otherwise, so they will NOT reconcile against a broker fill price for any symbol that has had
    /// a bonus or split. Use them for structure — levels, trends, indicators — and never to
    /// reconstruct what a trade actually cost. Fractional-paisa closes are the adjustment fingerprint.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<AhlCandle>> GetDailyCandlesAsync(
        string symbol, CancellationToken ct = default)
    {
        var response = await GetJsonAsync<AhlCandleResponse>(
            $"api/v3/market?path=/daily/{Uri.EscapeDataString(symbol)}", ct);
        return Reverse(response?.Data);
    }

    /// <summary>
    /// One-minute intraday candles, <b>oldest first</b> after reversal.
    /// </summary>
    /// <param name="range">
    /// Only <c>1D</c>, <c>2D</c> and <c>5D</c> exist — every other window (<c>1M</c>, <c>1Y</c>, …)
    /// answers 500. Longer history is the daily endpoint's job.
    /// </param>
    public async Task<IReadOnlyList<AhlCandle>> GetIntradayCandlesAsync(
        string symbol, string range = "1D", CancellationToken ct = default)
    {
        var normalised = range.ToUpperInvariant();
        if (normalised is not ("1D" or "2D" or "5D"))
        {
            _logger.LogWarning(
                "[AhlAnalytics] Intraday range '{Range}' is not supported by the portal " +
                "(only 1D, 2D, 5D); using 1D.", range);
            normalised = "1D";
        }

        var response = await GetJsonAsync<AhlCandleResponse>(
            $"api/v3/market?path=/intraday/{Uri.EscapeDataString(symbol)}/{normalised}", ct);
        return Reverse(response?.Data);
    }

    /// <summary>
    /// Flips the portal's newest-first ordering to the oldest-first every consumer expects. Done once
    /// here rather than at each call site, because a series read backwards produces indicators that
    /// look plausible and are wrong.
    /// </summary>
    private static IReadOnlyList<AhlCandle> Reverse(List<AhlCandle>? data)
    {
        if (data is null or { Count: 0 }) return [];
        var copy = new List<AhlCandle>(data);
        copy.Reverse();
        return copy;
    }

    // ── company statements ────────────────────────────────────────────────────

    /// <summary>
    /// A company statement matrix. <paramref name="type"/> is one of <c>fundamentals</c>,
    /// <c>income</c>, <c>balance</c>, <c>other</c>, <c>shareholders</c>;
    /// <paramref name="interval"/> is <c>annual</c> or <c>quarterly</c>.
    /// Only <c>type=fundamentals</c> carries <c>sector_stats</c>.
    /// </summary>
    public Task<AhlStatementResponse?> GetStatementAsync(
        string symbol, string type, string interval = "annual", CancellationToken ct = default) =>
        GetJsonAsync<AhlStatementResponse>(
            $"api/v3/company-statement?symbol={Uri.EscapeDataString(symbol)}" +
            $"&interval={Uri.EscapeDataString(interval)}&type={Uri.EscapeDataString(type)}", ct);

    /// <summary>Company profile — sector, description, fiscal year end, par value, employees.</summary>
    public Task<AhlProfileResponse?> GetProfileAsync(string symbol, CancellationToken ct = default) =>
        GetJsonAsync<AhlProfileResponse>(
            $"api/v3/company-statement?symbol={Uri.EscapeDataString(symbol)}" +
            "&interval=annual&type=profile", ct);

    // ── events ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Full payout and result history for a symbol — dividends, bonuses, rights, ex-dates and book
    /// closure windows, plus the parsed result figures. Hundreds of rows going back years.
    /// </summary>
    public async Task<IReadOnlyList<AhlAnnouncement>> GetPayoutHistoryAsync(
        string symbol, CancellationToken ct = default) =>
        await GetJsonAsync<List<AhlAnnouncement>>(
            $"api/v3/payouts/announcement-break-down/{Uri.EscapeDataString(symbol)}", ct) ?? [];

    /// <summary>Announcements for a symbol over a date window.</summary>
    public async Task<IReadOnlyList<AhlAnnouncement>> GetAnnouncementsAsync(
        string symbol, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        await GetJsonAsync<List<AhlAnnouncement>>(
            $"announcements/{Uri.EscapeDataString(symbol)}" +
            $"?rangeFrom={from:yyyy-MM-dd}&rangeTo={to:yyyy-MM-dd}&type=ALL", ct) ?? [];

    /// <summary>
    /// Market-wide UPCOMING board meetings. The pre-trade event-risk signal: a meeting scheduled for
    /// tomorrow is knowable today, and PSX day orders plus non-surviving protective stops mean an
    /// unplanned hold through one is real exposure.
    /// </summary>
    public async Task<IReadOnlyList<AhlAnnouncement>> GetBoardMeetingsAsync(
        CancellationToken ct = default) =>
        await GetJsonAsync<List<AhlAnnouncement>>("api/v1/announcements/board-meeting", ct) ?? [];

    /// <summary>Market-wide financial results as they post.</summary>
    public async Task<IReadOnlyList<AhlAnnouncement>> GetFinancialResultsAsync(
        CancellationToken ct = default) =>
        await GetJsonAsync<List<AhlAnnouncement>>("api/v1/announcements/financial-result", ct) ?? [];

    // ── insiders, news, research ───────────────────────────────────────────────

    /// <summary>
    /// Insider transactions, market-wide or for one symbol. Note the two dates on each row: key
    /// signals off <c>notice_date</c> (disclosure), not <c>date</c> (dealing) — they differ by days
    /// and only the former is when the market could have known.
    /// </summary>
    public async Task<IReadOnlyList<AhlInsiderTransaction>> GetInsiderTransactionsAsync(
        string? symbol = null, CancellationToken ct = default)
    {
        var path = "insider-transaction/api?sort=desc";
        if (!string.IsNullOrWhiteSpace(symbol)) path += "&symbol=" + Uri.EscapeDataString(symbol);
        return await GetJsonAsync<List<AhlInsiderTransaction>>(path, ct) ?? [];
    }

    /// <summary>News for a symbol, or market-wide with <c>GENERIC</c>.</summary>
    public async Task<IReadOnlyList<AhlNewsItem>> GetNewsAsync(
        string symbol = "GENERIC", CancellationToken ct = default)
    {
        var response = await PostJsonAsync<AhlNewsResponse>(
            $"api/v3/news/{Uri.EscapeDataString(symbol)}", null, ct);
        return response?.Data ?? [];
    }

    /// <summary>
    /// AHL's own analyst notes for a symbol — full body text, not just a PDF link. This is the
    /// broker's house view on instruments this account can actually trade.
    /// </summary>
    public async Task<IReadOnlyList<AhlResearchNote>> GetResearchNotesAsync(
        string symbol, int count = 10, CancellationToken ct = default) =>
        await GetJsonAsync<List<AhlResearchNote>>(
            $"client-research-v2/data/list?count={count}&offset=0" +
            $"&symbol={Uri.EscapeDataString(symbol)}", ct) ?? [];

    /// <summary>
    /// Precomputed indicators for the whole market.
    ///
    /// <para>
    /// <b>These disagree with indicators computed from the same vendor's candles</b> — MACFL on
    /// 2026-08-19 returned CCI −34.27 here against 215.23 computed locally, and RSI 78.22 against
    /// 76.84. The portal's own UI does not use this endpoint; it computes from candles. So this is a
    /// cross-check, not a source of truth, and <see cref="AhlAnalyticsConfig.PreferPortalIndicators"/>
    /// defaults to false for that reason.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<AhlSymbolIndicators>> GetIndicatorsAsync(
        CancellationToken ct = default)
    {
        var response = await GetJsonAsync<AhlIndicatorsResponse>("api/v3/indicators", ct);
        return response?.Data ?? [];
    }

    /// <summary>Macro indicators — GDP and similar, as reported by the portal's economy panel.</summary>
    public async Task<string?> GetEconomyDataRawAsync(CancellationToken ct = default) =>
        await SendAsync(HttpMethod.Get, "api/v3/economy-data", null, ct);

    public void Dispose()
    {
        _http.Dispose();
        _authGate.Dispose();
        _snapshotGate.Dispose();
    }
}
