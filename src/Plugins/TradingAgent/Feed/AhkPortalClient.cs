using System.Net;
using System.Text.Json;
using AgentFox.Plugins;
using Microsoft.Extensions.Logging;
using TradingAgent.Broker;
using TradingAgent.Config;

namespace TradingAgent.Feed;

/// <summary>
/// Talks to the AHK portal's JSON API (<c>/Home/*</c>) directly over HTTP, reusing the browser
/// broker's authenticated session. See <c>docs/ahk-feed-api.md</c> for the captured protocol.
///
/// <para>
/// <b>Why this is not another browser automation.</b> The portal's UI is entirely driven by JSON
/// endpoints, so anything the UI can do is reachable with an HttpClient and the session cookies —
/// at a few milliseconds per call instead of the seconds a page interaction costs, and without
/// taking the broker's single-page gate. What this class does NOT do is log in: that stays with
/// <see cref="AhkBroker"/>, which already solves the portal's positional-password form.
/// </para>
///
/// <para>
/// <b>Session lifecycle.</b> Cookies are harvested once, then kept alive by <c>POST /Home/Relogin</c>,
/// whose response body carries a status digit — <c>"0"</c> healthy, <c>"8"</c> dead. On <c>"8"</c>,
/// or on a response that redirects back to the login page, the cookies are dropped and re-harvested
/// from the browser on the next call. Everything here is fail-soft and returns null/empty rather
/// than throwing: the callers are a market-data poller and an order-cancel tool, and both have to
/// report a problem rather than propagate one.
/// </para>
/// </summary>
/// <summary>
/// The venue's own view of whether it is trading. Narrow on purpose: the order gate needs exactly
/// this one fact, and depending on the whole portal client would make the gate untestable and would
/// couple order policy to an HTTP client.
/// </summary>
public interface IBrokerMarketState
{
    /// <summary>
    /// Latest market status the broker reported ("OPEN", "CLOSED", "OHO", …), or null when it has
    /// not reported one — the feed being switched off, or not yet having polled.
    /// </summary>
    string? LastMarketStatus { get; }
}

public sealed class AhkPortalClient : IBrokerMarketState, IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    private readonly AhkBroker _broker;
    private readonly IRuntimePluginOptions<AhkConfig> _config;
    private readonly ILogger<AhkPortalClient> _logger;

    /// <summary>Serialises session (re)establishment so a burst of callers triggers one login, not five.</summary>
    private readonly SemaphoreSlim _sessionGate = new(1, 1);

    private HttpClient? _http;
    private CookieContainer? _cookies;
    private bool _sessionReady;

    public AhkPortalClient(
        AhkBroker broker,
        IRuntimePluginOptions<AhkConfig> config,
        ILogger<AhkPortalClient> logger)
    {
        _broker = broker;
        _config = config;
        _logger = logger;
    }

    /// <summary>The portal's base URI, from the same config the browser broker uses.</summary>
    public Uri BaseUri => new(_config.Current.PortalUrl.TrimEnd('/') + "/");

    /// <summary>
    /// The account code the portal is logged in as, read from the <c>trader</c> cookie it sets at
    /// login. The order endpoints want this value, and the portal's own UI reads it from a dropdown
    /// populated with exactly the same string — so taking it from the cookie avoids asking an
    /// operator to configure a number the session already knows.
    /// </summary>
    public string? AccountCode { get; private set; }

    /// <summary>Latest <c>marketStatus</c> string seen on a feed response ("OPEN", "CLOSED", "OHO", …).</summary>
    public string? LastMarketStatus { get; private set; }

    // ── Session ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Ensures an authenticated HTTP session, harvesting cookies from the browser broker when there
    /// isn't one. Returns false when a session could not be established — never throws.
    /// </summary>
    public async Task<bool> EnsureSessionAsync(CancellationToken ct = default)
    {
        if (_sessionReady && _http is not null) return true;

        await _sessionGate.WaitAsync(ct);
        try
        {
            if (_sessionReady && _http is not null) return true;

            var cookies = await _broker.GetSessionCookiesAsync(ct);
            if (cookies.Count == 0)
            {
                _logger.LogWarning("[AhkPortal] No session cookies available; the direct API stays offline.");
                return false;
            }

            var baseUri = BaseUri;
            var jar = new CookieContainer();
            foreach (var (name, value, domain) in cookies)
            {
                try
                {
                    // Puppeteer reports host-only cookies with a leading dot; CookieContainer treats
                    // that as a domain cookie and would then refuse to send it for the exact host.
                    var host = string.IsNullOrWhiteSpace(domain) ? baseUri.Host : domain.TrimStart('.');

                    // The value goes in VERBATIM. What the browser hands back is the on-the-wire
                    // cookie value, which ASP.NET Core has already percent-encoded — the session
                    // cookie really does contain literal "%2B" sequences. Re-encoding it here would
                    // turn those into "%252B", and the portal would treat every subsequent request as
                    // unauthenticated: a silent redirect to the login page, no error anywhere, and a
                    // feed that simply never produces a quote.
                    jar.Add(new Cookie(name, value, "/", host));
                }
                catch (Exception ex)
                {
                    // One malformed cookie must not cost the whole session — the ones that matter
                    // (.AspNetCore.Session, trader) are well-formed.
                    _logger.LogDebug(ex, "[AhkPortal] Skipped cookie {Name}.", name);
                }
            }

            _http?.Dispose();
            _cookies = jar;
            _http = new HttpClient(new SocketsHttpHandler
            {
                CookieContainer = jar,
                UseCookies = true,
                // The portal 302s to the login page when a session dies. Following that redirect
                // would turn "session expired" into a 200 full of HTML, which parses as no data and
                // looks exactly like a quiet market. Seeing the 302 is how expiry is detected.
                AllowAutoRedirect = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2)
            })
            {
                BaseAddress = baseUri,
                Timeout = TimeSpan.FromSeconds(20)
            };

            _http.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
            _http.DefaultRequestHeaders.Add("Accept", "*/*");
            _http.DefaultRequestHeaders.Referrer = new Uri(baseUri, "Home/Index");

            AccountCode = cookies.FirstOrDefault(c =>
                string.Equals(c.Name, "trader", StringComparison.OrdinalIgnoreCase)).Value is { Length: > 0 } t
                ? t
                : null;

            _sessionReady = true;
            _logger.LogInformation(
                "[AhkPortal] Direct API session established for account {Account}.",
                AccountCode ?? "(unknown)");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[AhkPortal] Could not establish a direct API session.");
            return false;
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    /// <summary>Forces the next call to re-harvest cookies from the browser.</summary>
    public void InvalidateSession(string reason)
    {
        if (!_sessionReady) return;
        _sessionReady = false;
        _logger.LogInformation("[AhkPortal] Direct API session invalidated: {Reason}", reason);
    }

    /// <summary>
    /// Keeps the session alive. Returns false when the portal says the session is finished, which is
    /// the caller's cue to stop polling until a fresh login. The portal answers with a bare string
    /// whose content is the status — <c>"8"</c> means logged out, <c>"0"</c> means fine.
    /// </summary>
    public async Task<bool> ReloginAsync(CancellationToken ct = default)
    {
        var body = await PostAsync("Home/Relogin", content: null, ct);
        if (body is null) return false;

        if (body.Contains('8'))
        {
            InvalidateSession("the portal reported the session as logged out (Relogin returned 8).");
            return false;
        }

        return true;
    }

    // ── Market data ───────────────────────────────────────────────────────────

    /// <summary>The full symbol master. Roughly 200 KB, so callers should fetch it once per session.</summary>
    public async Task<IReadOnlyList<AhkSymbolListEntry>> GetSymbolListAsync(CancellationToken ct = default)
    {
        // "GetSymolsList" is not a typo here — that is the portal's actual route spelling.
        var body = await GetAsync("Home/GetSymolsList", ct);
        return Deserialize<List<AhkSymbolListEntry>>(body, "GetSymolsList") ?? [];
    }

    /// <summary>
    /// Declares which symbols the session should receive quotes for, on one page slot. Replaces
    /// whatever that slot held. Returns false when the call failed.
    /// </summary>
    public async Task<bool> SubscribeAsync(
        string pageNum, IReadOnlyList<AhkSymbolKey> symbols, string feedType = "MKT-FEED",
        CancellationToken ct = default)
    {
        // jQuery serialises an array of objects as formData[i][key]; the portal's model binder reads
        // exactly that shape, so it has to be reproduced literally.
        var fields = new List<KeyValuePair<string, string>>(symbols.Count * 2 + 2);
        for (var i = 0; i < symbols.Count; i++)
        {
            fields.Add(new($"formData[{i}][mkt]", symbols[i].Market));
            fields.Add(new($"formData[{i}][symbol]", symbols[i].Symbol));
        }
        fields.Add(new("feedtype", feedType));
        fields.Add(new("pagenum", pageNum));

        var body = await PostAsync("Home/SendSubscriptionofSymbols", new FormUrlEncodedContent(fields), ct);
        return body is not null;
    }

    /// <summary>
    /// One poll of the live feed. Returns null when the call failed or the session died; an empty
    /// <see cref="AhkFeedResponse.Feed"/> is a normal, successful "nothing changed".
    /// </summary>
    public async Task<AhkFeedResponse?> GetFeedAsync(CancellationToken ct = default)
    {
        var body = await GetAsync("Home/GetFeed", ct);
        var parsed = Deserialize<AhkFeedResponse>(body, "GetFeed");
        if (parsed?.MarketStatus is { } status)
            LastMarketStatus = status.Replace("\r", "").Replace("\n", "").Trim();
        return parsed;
    }

    /// <summary>
    /// The whole market's daily price bands in one call — the ceiling a SELL and the floor a BUY may
    /// be priced at. PSX rejects anything outside the band.
    /// </summary>
    public async Task<IReadOnlyList<AhkPriceBand>> GetPriceBandsAsync(CancellationToken ct = default)
    {
        var body = await GetAsync("Home/GetUpperLowerCap", ct);
        return Deserialize<List<AhkPriceBand>>(body, "GetUpperLowerCap") ?? [];
    }

    // ── Orders ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The account's resting (unfilled) orders. <paramref name="symbol"/> empty means every symbol;
    /// <paramref name="orderType"/> is the portal's own vocabulary — <c>ALL</c>, <c>BUY</c> or
    /// <c>SEL</c> (three letters, not "SELL").
    /// </summary>
    public async Task<IReadOnlyList<AhkOutstandingOrder>> GetOutstandingAsync(
        string symbol = "", string orderType = "ALL", CancellationToken ct = default)
    {
        var account = AccountCode;
        if (string.IsNullOrWhiteSpace(account))
        {
            _logger.LogWarning("[AhkPortal] No account code available; cannot read the outstanding book.");
            return [];
        }

        var query = $"Home/GetOutstanding?symbol={Uri.EscapeDataString(symbol)}" +
                    $"&type={Uri.EscapeDataString(orderType)}" +
                    $"&account={Uri.EscapeDataString(account)}";

        var body = await GetAsync(query, ct);
        return Deserialize<List<AhkOutstandingOrder>>(body, "GetOutstanding") ?? [];
    }

    /// <summary>
    /// Requests cancellation of one resting order.
    ///
    /// <para>
    /// The boolean says the REQUEST was accepted, never that the order is gone. The portal returns
    /// no success indicator at all — its own UI fires this and immediately closes the dialog without
    /// looking at the response — so the only evidence of a cancellation is the order's absence from
    /// the outstanding book afterwards. Callers must verify there; see
    /// <c>TradingAgent.Tools.CancelOrderTool</c>.
    /// </para>
    /// </summary>
    public async Task<bool> CancelOrderAsync(string orderNo, CancellationToken ct = default)
    {
        // "orignalorderno" is the portal's spelling of the field. Correcting it silently no-ops.
        var content = new FormUrlEncodedContent([new KeyValuePair<string, string>("orignalorderno", orderNo)]);
        var body = await PostAsync("Home/CancelOrder", content, ct);
        return body is not null;
    }

    // ── Transport ─────────────────────────────────────────────────────────────

    private Task<string?> GetAsync(string path, CancellationToken ct) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Get, path), ct);

    private Task<string?> PostAsync(string path, HttpContent? content, CancellationToken ct) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Post, path) { Content = content }, ct);

    /// <summary>
    /// Sends one request against the authenticated session. Returns the body, or null when the call
    /// could not be completed — including when the session has expired, in which case the session is
    /// invalidated so the next caller re-harvests cookies.
    /// </summary>
    private async Task<string?> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (!await EnsureSessionAsync(ct)) return null;

        var http = _http;
        if (http is null) return null;

        try
        {
            using var response = await http.SendAsync(request, ct);

            // A dead session is a redirect to the login page, not an error status.
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                InvalidateSession($"the portal redirected to {response.Headers.Location} (session expired).");
                return null;
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                InvalidateSession($"the portal answered {(int)response.StatusCode}.");
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[AhkPortal] {Method} {Path} returned {Status}.",
                    request.Method, request.RequestUri, (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AhkPortal] {Method} {Path} failed.", request.Method, request.RequestUri);
            return null;
        }
        finally
        {
            request.Dispose();
        }
    }

    /// <summary>
    /// Parses a portal response, treating unparseable content as absent data rather than an error.
    /// The portal serves an HTML login page with HTTP 200 in some expiry cases, so "this is not the
    /// JSON I expected" is a routine condition and must not throw into a polling loop.
    /// </summary>
    private T? Deserialize<T>(string? body, string endpoint) where T : class
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        try
        {
            return JsonSerializer.Deserialize<T>(body, Json);
        }
        catch (JsonException)
        {
            if (body.Contains("<html", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("_Login", StringComparison.OrdinalIgnoreCase))
            {
                InvalidateSession($"{endpoint} answered with the login page instead of JSON.");
                return null;
            }

            _logger.LogWarning("[AhkPortal] {Endpoint} returned unparseable JSON: {Snippet}",
                endpoint, body.Length > 200 ? body[..200] : body);
            return null;
        }
    }

    public void Dispose()
    {
        _http?.Dispose();
        _sessionGate.Dispose();
    }
}
