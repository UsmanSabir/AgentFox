using System.Globalization;
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

    /// <summary>
    /// Whether an authenticated HTTP session already exists, WITHOUT establishing one.
    ///
    /// <para>
    /// This is the guard for passive, periodic readers. <see cref="EnsureSessionAsync"/> harvests its
    /// cookies from the browser broker, and that harvest calls the broker's session preparation —
    /// which launches Chromium and performs a full portal LOGIN when no session is live. A caller on
    /// a timer that ignored this would therefore turn a dead session into a login attempt on every
    /// tick: at the reconciliation worker's 60-second cadence that is sixty logins an hour, against a
    /// broker that has already withdrawn access once for far less (see docs/phase-b-runbook.md §0).
    /// </para>
    ///
    /// <para>
    /// So: anything user-initiated may call <see cref="EnsureSessionAsync"/> and pay for a login;
    /// anything on a timer must check this first and report "no session" rather than create one.
    /// </para>
    /// </summary>
    public bool HasSession => _sessionReady && _http is not null;

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
                // Authenticate to an authenticating corporate proxy with the process's own Windows
                // identity, which is what the browser alongside us already does.
                //
                // Without this, every call on a network with a system proxy fails at the tunnel with
                // 407 before it ever reaches the portal — and the symptom is maximally misleading:
                // the session is reported as ESTABLISHED (cookies harvested from Chromium succeeded,
                // that part never touches the proxy), and only the subsequent requests fail, so the
                // feed looks like a broker that has gone quiet rather than a proxy that said no.
                // Observed on a NETSOL workstation on 2026-08-18, where the bypass list covers many
                // hosts but not the broker's. Null credentials are harmless where no proxy exists.
                DefaultProxyCredentials = CredentialCache.DefaultCredentials,
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
    public async Task<OrderBookRead> GetOutstandingAsync(
        string symbol = "", string orderType = "ALL", CancellationToken ct = default)
    {
        // Establish the session FIRST. AccountCode comes from the login cookies, so reading it before
        // a session exists always yields null — which made the order tools depend on the feed worker
        // having run first, and fail outright whenever the feed was switched off. Every other call
        // here gets its session lazily inside SendAsync; this one needs it a step earlier because it
        // must put the account code into the query string.
        if (!await EnsureSessionAsync(ct))
        {
            return OrderBookRead.Failed(
                "Could not establish a broker session, so the outstanding order book could not be read.");
        }

        var account = AccountCode;
        if (string.IsNullOrWhiteSpace(account))
        {
            _logger.LogWarning("[AhkPortal] Session established but no account code was returned.");
            return OrderBookRead.Failed(
                "The broker session carries no account code, so the outstanding order book could not be read.");
        }

        var query = $"Home/GetOutstanding?symbol={Uri.EscapeDataString(symbol)}" +
                    $"&type={Uri.EscapeDataString(orderType)}" +
                    $"&account={Uri.EscapeDataString(account)}";

        var body = await GetAsync(query, ct);
        if (body is null)
        {
            // Null means the call did not complete: expired session, redirect to login, HTTP error,
            // or access withdrawn by the broker. Returning an empty list here would be indistinguishable
            // from "no working orders" — see OrderBookRead for what that cost when it happened.
            return OrderBookRead.Failed(
                "The broker did not return the outstanding order book (the session may have expired " +
                "or account access may be blocked). The account's real orders are UNKNOWN.");
        }

        var parsed = Deserialize<List<AhkOutstandingOrder>>(body, "GetOutstanding");
        return parsed is null
            ? OrderBookRead.Failed(
                "The broker's outstanding-order response could not be parsed. The account's real " +
                "orders are UNKNOWN.")
            : OrderBookRead.Success(parsed);
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

    // ── Account state ─────────────────────────────────────────────────────────

    /// <summary>
    /// Establishes the session and returns the account code the account-scoped endpoints require.
    /// Returns null when either step fails; the caller must treat that as "unknown", never as empty.
    /// </summary>
    private async Task<string?> ResolveAccountAsync(CancellationToken ct)
    {
        if (!await EnsureSessionAsync(ct)) return null;

        var account = AccountCode;
        if (!string.IsNullOrWhiteSpace(account)) return account;

        _logger.LogWarning("[AhkPortal] Session established but no account code was returned.");
        return null;
    }

    /// <summary>
    /// Available cash, in PKR, from <c>GET /Home/GetAccountBalance?account=…</c>. Returns null when
    /// the balance could not be read — which a caller must report as unknown rather than as zero.
    ///
    /// <para>
    /// The portal answers with a JSON <b>string</b> holding the number (<c>"78141.0"</c>), not a JSON
    /// number, which is why this parses rather than deserialising to <c>decimal</c>. Its own UI does
    /// <c>Number(res)</c> for the same reason.
    /// </para>
    /// </summary>
    public async Task<decimal?> GetAccountBalanceAsync(CancellationToken ct = default)
    {
        var account = await ResolveAccountAsync(ct);
        if (account is null) return null;

        var body = await GetAsync($"Home/GetAccountBalance?account={Uri.EscapeDataString(account)}", ct);
        if (string.IsNullOrWhiteSpace(body)) return null;

        var value = ParseBalance(body);
        if (value is not null) return value;

        _logger.LogWarning("[AhkPortal] GetAccountBalance returned an unparseable value: {Body}",
            body.Length > 120 ? body[..120] : body);
        return null;
    }

    /// <summary>
    /// Parses the balance payload. Split out from the call so it can be exercised against captured
    /// responses without a live session — it is the one piece of this file with a format quirk worth
    /// pinning down, and getting it wrong yields a plausible-looking wrong number rather than an error.
    ///
    /// <para>
    /// Handles the quoted form the portal actually sends (<c>"78141.0"</c>), a bare number should it
    /// ever send one, and thousands separators. Invariant culture is forced: under a comma-decimal
    /// locale, stripping separators first and then parsing "78141.0" leniently is how a balance
    /// silently becomes 781410.
    /// </para>
    /// </summary>
    internal static decimal? ParseBalance(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        var raw = body.Trim().Trim('"').Trim().Replace(",", "");
        if (raw.Length == 0) return null;

        return decimal.TryParse(
            raw,
            NumberStyles.Float | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;
    }

    /// <summary>
    /// The account's holdings, from <c>GET /Home/GetCollaterals?account=…</c>. Null means the read
    /// failed; an empty list means the account genuinely holds nothing. Keeping those apart matters
    /// for exactly the reason spelled out on <see cref="OrderBookRead"/>.
    /// </summary>
    public async Task<IReadOnlyList<AhkCollateralHolding>?> GetCollateralsAsync(CancellationToken ct = default)
    {
        var account = await ResolveAccountAsync(ct);
        if (account is null) return null;

        var body = await GetAsync($"Home/GetCollaterals?account={Uri.EscapeDataString(account)}", ct);
        if (body is null) return null;

        return Deserialize<List<AhkCollateralHolding>>(body, "GetCollaterals");
    }

    /// <summary>
    /// Today's order-lifecycle events from <c>GET /Home/GetActivityLog</c>. Null means the read
    /// failed. <paramref name="orderType"/> is the portal's vocabulary — <c>ALL</c>, <c>BUY</c>
    /// or <c>SEL</c>.
    /// </summary>
    public async Task<IReadOnlyList<AhkActivityLogEntry>?> GetActivityLogAsync(
        string symbol = "", string orderType = "ALL", CancellationToken ct = default)
    {
        var account = await ResolveAccountAsync(ct);
        if (account is null) return null;

        var body = await GetAsync(
            $"Home/GetActivityLog?symbol={Uri.EscapeDataString(symbol)}" +
            $"&type={Uri.EscapeDataString(orderType)}" +
            $"&account={Uri.EscapeDataString(account)}", ct);
        if (body is null) return null;

        return Deserialize<List<AhkActivityLogEntry>>(body, "GetActivityLog");
    }

    /// <summary>
    /// Today's executions from <c>GET /Home/GetTradeLog</c>. Null means the read failed; empty means
    /// nothing filled today. The populated shape is unverified — see <see cref="AhkTradeLogEntry"/>.
    /// </summary>
    public async Task<IReadOnlyList<AhkTradeLogEntry>?> GetTradeLogAsync(
        string symbol = "", string orderType = "ALL", CancellationToken ct = default)
    {
        var account = await ResolveAccountAsync(ct);
        if (account is null) return null;

        var body = await GetAsync(
            $"Home/GetTradeLog?symbol={Uri.EscapeDataString(symbol)}" +
            $"&type={Uri.EscapeDataString(orderType)}" +
            $"&account={Uri.EscapeDataString(account)}", ct);
        if (body is null) return null;

        return Deserialize<List<AhkTradeLogEntry>>(body, "GetTradeLog");
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
