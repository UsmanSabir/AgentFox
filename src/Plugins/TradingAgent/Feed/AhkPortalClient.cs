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

    /// <summary>
    /// Increments every time a session is established, so callers can tell a NEW session from the one
    /// they were using.
    ///
    /// <para>
    /// This exists because subscriptions live on the session: a re-established session starts with
    /// none, and the feed worker's cached "these symbols are subscribed" is then a belief about a
    /// session that no longer exists. Nothing reports that — <c>GetFeed</c> answers 200 with an empty
    /// array whether nothing traded or nothing is subscribed — so without this the recovery is the
    /// silence watchdog, which by design costs thirty quiet polls of an open market first.
    /// </para>
    /// </summary>
    public int SessionEpoch { get; private set; }

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
            SessionEpoch++;
            _logger.LogInformation(
                "[AhkPortal] Direct API session established for account {Account} (session #{Epoch}).",
                AccountCode ?? "(unknown)", SessionEpoch);
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

        // An empty body here means SUCCESS — the exact opposite of what it means on PlaceOrder, where it is
        // how the endpoint refuses. Confirmed live on 2026-08-19: a subscription of 30 symbols answered 200
        // with zero bytes and every one of the 30 then streamed. So this really is "did the call complete",
        // and it must not be "hardened" by treating an empty body as failure — that would turn every
        // successful subscription into a retry loop.
        //
        // What it genuinely cannot tell is whether the portal HONOURED the subscription. The only proof of
        // that is quotes arriving, which is why AhkFeedWorker compares what it subscribed against what the
        // feed returns rather than trusting this bool.
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
    // ── Order placement ───────────────────────────────────────────────────────

    /// <summary>
    /// Submits one order over the portal's JSON API and reports what can actually be known about it.
    ///
    /// <para>
    /// <b>Everything here is shaped by what a live capture on 2026-08-19 showed</b> (see
    /// <c>docs/ahk-direct-api-migration.md</c>), because none of it is guessable:
    /// </para>
    /// <list type="bullet">
    /// <item>An order that QUEUED and an order the exchange REJECTED return byte-identical responses —
    /// <c>200</c> with <c>"Order has been sent to Trade Server."</c>. The body is a transmission receipt.
    /// Reading it as a verdict is the mistake the portal's own UI makes.</item>
    /// <item>An <b>empty</b> body is different, and it means the endpoint refused the request outright:
    /// nothing in the order book, and no activity row at all. That is what a bad <c>OrderType</c> string
    /// produces — <c>"Stop Loss"</c> with a space vanished silently, <c>"StopLoss"</c> worked.</item>
    /// <item>The verdict lives in <c>GetActivityLog</c>'s <c>action</c> against the order number:
    /// <c>QUE</c> queued, <c>APT</c> accepted, <c>REJ</c> rejected, <c>CLX</c> cancelled. A rejected
    /// order still gets an order number, so absence from the outstanding book is not proof of anything on
    /// its own.</item>
    /// </list>
    ///
    /// <para>
    /// So this never returns a bare bool. <see cref="PlaceOrderApiResult.Submitted"/> answers "did the
    /// endpoint accept the request", which is all the response can support, and
    /// <see cref="PlaceOrderApiResult.Action"/> carries the exchange's verdict when the activity log has
    /// caught up. A caller must treat <c>Submitted = true</c> with a null action as UNKNOWN and reconcile,
    /// never as either outcome.
    /// </para>
    /// </summary>
    public async Task<PlaceOrderApiResult> PlaceOrderAsync(
        PlaceOrderApiRequest request, CancellationToken ct = default)
    {
        var account = await ResolveAccountAsync(ct);
        if (account is null)
            return new PlaceOrderApiResult(false, null, null, null,
                "No broker session, so the order was never sent.");

        // Order numbers already on record for this symbol BEFORE submitting. The activity log gives no
        // way to ask "which row is mine", so the discriminator is "a number that was not there before" —
        // the same technique the live capture harness uses, and the only one that survives a second
        // identical order later in the day.
        var before = await ActivityOrderNumbersAsync(account, request.Symbol, ct);

        var fields = new List<KeyValuePair<string, string>>
        {
            new("Account",    account),
            new("BuySell",    request.Side),
            new("Market",     request.Market),
            new("OrderType",  request.OrderType),
            new("Volume",     request.Volume.ToString(CultureInfo.InvariantCulture)),
            new("Script",     request.Symbol.Trim().ToUpperInvariant()),
            new("Exchange",   "KSE"),
            new("Price",      request.Price.ToString("F2", CultureInfo.InvariantCulture)),
            new("PIN",        request.Pin),
            new("LimitPrice", request.LimitPrice?.ToString("F2", CultureInfo.InvariantCulture) ?? "")
        };

        var body = await PostAsync("Home/PlaceOrder", new FormUrlEncodedContent(fields), ct);
        if (body is null)
            return new PlaceOrderApiResult(false, null, null, null,
                "The PlaceOrder call itself failed, so the order was not submitted.");

        // The acknowledgement is WHITELISTED rather than failures blacklisted, because the set of things
        // this endpoint says when it refuses is open-ended and was discovered one surprise at a time: an
        // empty body for a field it would not accept, and "Market is closed" off-hours. Treating
        // "not a known refusal" as submitted would mean every future refusal message becomes a phantom
        // order in the ledger. Only one string means the order reached the trade server.
        var said = body.Trim().Trim('"').Trim();

        if (said.Length == 0)
            return new PlaceOrderApiResult(false, body, null, null,
                "The portal answered with an empty body, which is how it refuses a request whose FIELDS it "
              + "will not accept — OrderType is the usual culprit. Nothing was placed.")
            { RefusedByFieldEncoding = true };

        if (!said.Contains("sent to trade server", StringComparison.OrdinalIgnoreCase))
            return new PlaceOrderApiResult(false, body, null, null,
                $"The portal refused the order and said so: \"{said}\". Nothing was placed.");

        var (orderNo, action) = await AwaitOrderVerdictAsync(account, request.Symbol, before, ct);

        return new PlaceOrderApiResult(
            Submitted: true,
            RawBody: body,
            OrderNo: orderNo,
            Action: action,
            Message: action switch
            {
                null      => $"Submitted ({body.Trim('"')}) but no activity row appeared yet, so the "
                           + "outcome is UNKNOWN and must be reconciled.",
                "REJ"     => $"The exchange REJECTED the order (order no {orderNo}).",
                "CLX"     => $"The order was cancelled (order no {orderNo}).",
                _         => $"The order is live at the exchange as '{action}' (order no {orderNo})."
            });
    }

    /// <summary>
    /// Waits briefly for the submitted order to appear in the activity log and returns its number and
    /// action. Bounded and fail-soft: an order whose row has not arrived is UNKNOWN, and saying so is
    /// more useful than blocking a trading loop or inventing a verdict.
    /// </summary>
    private async Task<(string? OrderNo, string? Action)> AwaitOrderVerdictAsync(
        string account, string symbol, ISet<string> before, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(1_000, ct);

            var rows = await GetActivityLogAsync(symbol, "ALL", ct);
            var mine = rows?
                .Where(r => !string.IsNullOrWhiteSpace(r.OrderNo)
                         && !before.Contains(r.OrderNo!.Trim())
                         && string.Equals(r.Scrip?.Trim(), symbol.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (mine is { Count: > 0 })
            {
                // Newest row wins: an order can be QUE first and CLX or REJ afterwards, and the latest
                // action is the one that describes where it stands now.
                var latest = mine.OrderByDescending(r => r.Time ?? "").First();
                return (latest.OrderNo?.Trim(), latest.Action?.Trim().ToUpperInvariant());
            }
        }

        return (null, null);
    }

    /// <summary>Order numbers the activity log already holds for a symbol, used as the "before" baseline.</summary>
    private async Task<ISet<string>> ActivityOrderNumbersAsync(
        string account, string symbol, CancellationToken ct)
    {
        var rows = await GetActivityLogAsync(symbol, "ALL", ct);
        return rows?
            .Where(r => !string.IsNullOrWhiteSpace(r.OrderNo)
                     && string.Equals(r.Scrip?.Trim(), symbol.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(r => r.OrderNo!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

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

    /// <summary>
    /// Hop ① of the AHL Analytics SSO handshake: asks the trading portal for a pre-signed URL into
    /// the research portal (<c>data.arifhabibltd.com</c>) and returns it.
    ///
    /// <para>
    /// The portal's own "AHL Analytics" button does exactly this — <c>OpenAnalytics()</c> GETs this
    /// endpoint and <c>window.open</c>s the answer. The body is a JSON <b>string</b> (quoted), not an
    /// object, and it carries a Laravel-encrypted blob identifying the trader. Two properties matter
    /// to callers: the URL is returned with an <c>http://</c> scheme that redirects to https, and the
    /// blob is REPLAYABLE — refetching the same URL yields the same downstream token — so it may be
    /// cached rather than re-minted per request.
    /// </para>
    ///
    /// <para>
    /// Lives here rather than in the analytics client because it is a <c>/Home/*</c> endpoint on the
    /// broker's session, and this class is the one thing that owns that session.
    /// </para>
    /// </summary>
    /// <returns>The absolute analytics URL, or null when the session is unavailable or the portal
    /// answered with something other than a URL.</returns>
    public async Task<string?> GetAnalyticsUrlAsync(CancellationToken ct = default)
    {
        var body = await GetAsync("Home/GetAnalyticsURL", ct);
        if (string.IsNullOrWhiteSpace(body)) return null;

        // The response is a bare JSON string. Deserialize rather than trimming quotes by hand so an
        // escaped character in the token does not survive into the URL.
        string? url;
        try
        {
            url = JsonSerializer.Deserialize<string>(body, Json);
        }
        catch (JsonException)
        {
            // A dead session serves the login page with HTTP 200 here, same as everywhere else.
            if (body.Contains("<html", StringComparison.OrdinalIgnoreCase))
                InvalidateSession("GetAnalyticsURL answered with the login page instead of a URL.");
            else
                _logger.LogWarning("[AhkPortal] GetAnalyticsURL returned unparseable body: {Snippet}",
                    body.Length > 160 ? body[..160] : body);
            return null;
        }

        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            _logger.LogWarning("[AhkPortal] GetAnalyticsURL returned no usable URL.");
            return null;
        }

        // Force https. The portal hands out http:// and relies on a 307, which would put the
        // trader-identifying blob on the wire in cleartext for one hop.
        if (parsed.Scheme == Uri.UriSchemeHttp)
            parsed = new UriBuilder(parsed) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;

        return parsed.ToString();
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

/// <summary>
/// One order as the portal's <c>PlaceOrder</c> endpoint wants it. Field names and encodings are the
/// portal's, not ours — see <see cref="AhkPortalClient.PlaceOrderAsync"/> for why each is what it is.
/// </summary>
/// <param name="Side">
/// <c>"BUY"</c>, <c>"SEL"</c> or <c>"SHS"</c>. Not <c>"SELL"</c> — the portal is asymmetric here, and
/// <see cref="AhkOrderTypes"/> exists so no caller has to remember that.
/// </param>
/// <param name="OrderType">Use <see cref="AhkOrderTypes"/>; a wrong string is silently discarded.</param>
/// <param name="Price">The limit price, or the TRIGGER price for a stop order.</param>
/// <param name="LimitPrice">The limit for a stop order. Null (sent as empty) for every other type.</param>
public sealed record PlaceOrderApiRequest(
    string Side,
    string Symbol,
    string Market,
    string OrderType,
    int Volume,
    decimal Price,
    decimal? LimitPrice,
    string Pin);

/// <summary>
/// What is actually knowable about a submitted order. <paramref name="Submitted"/> is about the REQUEST
/// (did the endpoint accept it), <paramref name="Action"/> about the ORDER (what the exchange did with
/// it). Submitted with a null action is UNKNOWN, and must be reconciled rather than assumed either way.
/// </summary>
public sealed record PlaceOrderApiResult(
    bool Submitted,
    string? RawBody,
    string? OrderNo,
    string? Action,
    string Message)
{
    /// <summary>Actions that mean the order is live at the exchange. Confirmed live: QUE, APT.</summary>
    public bool IsLive => Action is "QUE" or "APT";

    /// <summary>Actions that mean the order is definitively not working. Confirmed live: REJ, CLX.</summary>
    public bool IsDead => Action is "REJ" or "CLX";

    /// <summary>
    /// True when the endpoint refused the request with an EMPTY body, which is what it does when a field
    /// is encoded in a way it will not take.
    ///
    /// <para>
    /// This is the only refusal worth retrying through the browser, and that is the whole reason it is a
    /// separate flag rather than folded into <see cref="Submitted"/>. The order dialog builds the same
    /// request from the portal's own selects, so it does not get the encoding wrong — whereas a refusal the
    /// portal EXPLAINS ("Market is closed") will be refused identically no matter which path asks, and
    /// retrying it just launches a browser to be told no twice.
    /// </para>
    /// </summary>
    public bool RefusedByFieldEncoding { get; init; }
}

/// <summary>
/// The exact <c>OrderType</c> strings the endpoint accepts, all confirmed against the live portal.
///
/// <para>
/// This is a named constant rather than a literal at each call site because the failure mode is invisible:
/// a wrong string returns HTTP 200 with an empty body, no order, and no activity row. <c>"Stop Loss"</c>
/// — the label the portal shows its own users, and what its <c>site.js</c> sends on the SELL side — is one
/// of the wrong ones. <c>"StopLoss"</c>, the underlying option VALUE, is what works.
/// </para>
/// </summary>
public static class AhkOrderTypes
{
    /// <summary>Confirmed accepted on both BUY and SELL.</summary>
    public const string Limit = "Limit";

    /// <summary>Confirmed accepted as a SELL stop (trigger in Price, limit in LimitPrice).</summary>
    public const string StopLoss = "StopLoss";

    /// <summary>
    /// NOT VERIFIED against the live portal. Left here named so a caller reaching for it finds this
    /// sentence first: capture one before trusting it, because the way this endpoint says no is to say
    /// nothing at all.
    /// </summary>
    public const string MarketUnverified = "Market";
}

