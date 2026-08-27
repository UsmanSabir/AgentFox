using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using AgentFox.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TradingAgent.Broker;
using TradingAgent.Config;
using TradingAgent.Feed;
using TradingAgent.Models;

namespace AgentFox.ChannelTests;

/// <summary>
/// Opt-in harness for capturing what the AHK portal's JSON API actually does, against a real account.
///
/// <para>
/// <b>Why it reads the app's own config file.</b> The other external tests take credentials from
/// <c>AHK_TEST_*</c> environment variables, which means whoever runs them has to handle the password.
/// These read <c>appsettings.user.json</c> — the file the agent itself runs on — so the credentials go
/// from disk into the process and nowhere else: not into a shell history, not into a transcript, not
/// into a CI log.
/// </para>
///
/// <para>
/// <b>Everything here is gated.</b> Nothing runs without <c>AHK_LIVE_CAPTURE=true</c>, and the test
/// that submits orders additionally requires each order to be described explicitly — there is no
/// default symbol, quantity or price, because a default would eventually place an order nobody chose.
/// </para>
/// </summary>
[TestClass]
public sealed class AhkLiveCaptureTests
{
    private const string EnabledVar = "AHK_LIVE_CAPTURE";
    private const string ConfigVar  = "AHK_LIVE_CONFIG";

    // ── Read-only: account state, and the feed subscription diagnosis ─────────

    /// <summary>
    /// Logs in, then reports what the account holds and what the feed endpoints actually answer.
    /// Places nothing. This is the pass that distinguishes "subscribed and quiet" from "not
    /// subscribed" — the two states the portal reports identically, as an empty 200.
    /// </summary>
    [TestMethod]
    [TestCategory("External")]
    [TestCategory("AhkLive")]
    public async Task Report_AccountState_And_FeedDiagnostics()
    {
        var (config, hostConfig) = LoadLiveConfig();

        await using var broker = new AhkBroker(
            new FixedRuntimeOptions(config), hostConfig, NullLogger<AhkBroker>.Instance);

        using var http = await OpenSessionAsync(broker);

        Report("ACCOUNT BALANCE", await GetAsync(http, $"Home/GetAccountBalance?account={Account(http)}"));
        Report("HOLDINGS (GetCollaterals)", await GetAsync(http, $"Home/GetCollaterals?account={Account(http)}"));
        Report("OUTSTANDING (resting orders)", await GetAsync(http, $"Home/GetOutstanding?account={Account(http)}"));

        // ── The feed diagnosis ───────────────────────────────────────────────
        // GetFeed BEFORE any subscription of ours, so the baseline is on record: whatever the portal is
        // already streaming to this session is what a previous page load left subscribed.
        Report("GetFeed (before we subscribe)", await GetAsync(http, "Home/GetFeed"));

        var symbols = (Environment.GetEnvironmentVariable("AHK_LIVE_FEED_SYMBOLS") ?? "OGDC,PPL,MARI")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // One at a time as well as together. If a single bad symbol/market pair makes the portal drop
        // the WHOLE batch — the leading suspect for a 30-symbol subscription that produces nothing —
        // then the per-symbol calls succeed where the batch call does not, and that is the whole
        // diagnosis. Sending only the batch cannot tell those apart.
        foreach (var symbol in symbols)
        {
            Report($"SUBSCRIBE single [{symbol}]", await SubscribeAsync(http, "Page1", [symbol]));
            await Task.Delay(2_500);
            Report($"GetFeed after single [{symbol}]", await GetAsync(http, "Home/GetFeed"));
        }

        Report($"SUBSCRIBE batch [{string.Join(",", symbols)}]", await SubscribeAsync(http, "Page1", symbols));
        for (var i = 1; i <= 3; i++)
        {
            await Task.Delay(2_500);
            Report($"GetFeed after batch, poll {i}", await GetAsync(http, "Home/GetFeed"));
        }

        Report("PRICE BANDS (GetUpperLowerCap, first 600 chars)",
            Trim(await GetAsync(http, "Home/GetUpperLowerCap"), 600));
    }

    /// <summary>
    /// Subscribes the whole monitored universe, then reports which symbols the feed actually returns and
    /// which it does not — and re-tests each missing symbol on its own.
    ///
    /// <para>
    /// This is the pass that answers "the feed returned nothing for 30 consecutive polls". `GetFeed`
    /// returns one snapshot row per SUBSCRIBED symbol whether or not it has traded, so a symbol absent
    /// from the response is a symbol the portal did not subscribe — and re-testing it alone separates
    /// "this symbol is unsubscribable" from "this symbol took the whole batch down with it".
    /// </para>
    /// </summary>
    [TestMethod]
    [TestCategory("External")]
    [TestCategory("AhkLive")]
    public async Task Diagnose_FeedSubscription_Coverage()
    {
        // Gate FIRST. Reading the symbol list before it turned a "not enabled, skip me" into a hard test
        // failure on every ordinary run of the suite.
        var (config, hostConfig) = LoadLiveConfig();

        var symbolList = Environment.GetEnvironmentVariable("AHK_LIVE_FEED_SYMBOLS");
        if (string.IsNullOrWhiteSpace(symbolList))
            Assert.Inconclusive("Set AHK_LIVE_FEED_SYMBOLS to the universe to test.");

        var requested = symbolList!
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToUpperInvariant()).Distinct().ToList();
        await using var broker = new AhkBroker(
            new FixedRuntimeOptions(config), hostConfig, NullLogger<AhkBroker>.Instance);
        using var http = await OpenSessionAsync(broker);

        Report($"SUBSCRIBE all {requested.Count}", await SubscribeAsync(http, "Page1", requested));

        // Union across several polls: a snapshot can legitimately arrive in pieces, and calling a symbol
        // missing after one poll would invent a problem.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i <= 4; i++)
        {
            await Task.Delay(2_500);
            var body = StripStatus(await GetAsync(http, "Home/GetFeed"));
            foreach (var s in FeedSymbols(body)) seen.Add(s);
            Report($"poll {i}", $"{seen.Count}/{requested.Count} symbols seen so far");
        }

        var missing = requested.Where(s => !seen.Contains(s)).ToList();
        Report("COVERAGE",
            $"requested={requested.Count} received={seen.Count}\n"
          + $"missing={(missing.Count == 0 ? "(none)" : string.Join(", ", missing))}");

        // Each missing symbol, alone. Alone-and-works means the batch is the problem; alone-and-fails
        // names a symbol the portal will not stream, which is a watchlist fix rather than a code fix.
        foreach (var symbol in missing)
        {
            Report($"SUBSCRIBE alone [{symbol}]", await SubscribeAsync(http, "Page1", [symbol]));
            await Task.Delay(2_500);
            var body = StripStatus(await GetAsync(http, "Home/GetFeed"));
            var got = FeedSymbols(body).ToList();
            Report($"alone [{symbol}]", got.Contains(symbol, StringComparer.OrdinalIgnoreCase)
                ? "STREAMS when subscribed alone — so the batch is what fails"
                : $"does NOT stream even alone (feed returned: {(got.Count == 0 ? "nothing" : string.Join(",", got))})");
        }

        // Leave the session holding the full set rather than the last single-symbol probe.
        Report("RESUBSCRIBE full set", await SubscribeAsync(http, "Page1", requested));
    }

    /// <summary>Symbol names present in a <c>GetFeed</c> response's <c>feed</c> array.</summary>
    private static IEnumerable<string> FeedSymbols(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); } catch { yield break; }
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("feed", out var feed) || feed.ValueKind != JsonValueKind.Array)
                yield break;
            foreach (var row in feed.EnumerateArray())
                if (row.TryGetProperty("symbol", out var s) && s.GetString() is { Length: > 0 } name)
                    yield return name.ToUpperInvariant();
        }
    }

    // ── Order submission: the one unknown that needs a real order ─────────────

    /// <summary>
    /// Submits the orders described by <c>AHK_LIVE_ORDERS</c>, records exactly what
    /// <c>POST /Home/PlaceOrder</c> answers, verifies each against the account's own order book, and
    /// then cancels every order it placed — including on failure, which is why the cancel pass is in a
    /// finally rather than at the end of the happy path.
    /// </summary>
    /// <remarks>
    /// <c>AHK_LIVE_ORDERS</c> is a JSON array, one object per order:
    /// <c>[{"side":"BUY","symbol":"OGDC","volume":1,"price":"100.00","market":"REG","orderType":"Limit"}]</c>
    /// There is deliberately no default: every field of a real order is chosen by a human.
    /// </remarks>
    [TestMethod]
    [TestCategory("External")]
    [TestCategory("AhkLiveOrders")]
    public async Task Place_Capture_And_Cancel_Orders()
    {
        var spec = Environment.GetEnvironmentVariable("AHK_LIVE_ORDERS");
        if (string.IsNullOrWhiteSpace(spec))
            Assert.Inconclusive("Set AHK_LIVE_ORDERS to a JSON array of orders to submit. See the remarks.");

        var orders = JsonSerializer.Deserialize<List<OrderSpec>>(spec!,
                         new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                     ?? throw new InvalidOperationException("AHK_LIVE_ORDERS did not parse.");
        Assert.IsTrue(orders.Count > 0, "AHK_LIVE_ORDERS parsed to an empty list.");

        var (config, hostConfig) = LoadLiveConfig();
        Assert.IsFalse(string.IsNullOrWhiteSpace(config.TradingPin),
            "Plugins:Ahk:TradingPin is required to submit an order.");

        await using var broker = new AhkBroker(
            new FixedRuntimeOptions(config), hostConfig, NullLogger<AhkBroker>.Instance);

        using var http = await OpenSessionAsync(broker);
        var account = Account(http);

        var before = await GetAsync(http, $"Home/GetOutstanding?account={account}");
        Report("OUTSTANDING before", before);

        // Every order number the account ALREADY has resting. The cancel pass may only touch numbers
        // that are not in this set: this account carries genuine protective sells placed by the agent
        // (a live MARI sell was resting when this harness was written), and a cleanup pass that cancels
        // "every resting order on a symbol I traded" would silently remove a real stop and leave the
        // position unprotected. Cancelling too little is recoverable by hand; cancelling a stop is not.
        var preexisting = AllOrderNumbers(before).ToHashSet();
        Report("PRE-EXISTING ORDERS (will NOT be cancelled)",
            preexisting.Count == 0 ? "(none)" : string.Join(", ", preexisting));

        // Bands for the whole market in one call. An order with no explicit price is priced AT its band
        // edge — a SELL at the upper cap, a BUY at the lower lock — which is the only price that is both
        // inside the band the portal enforces and far enough from the touch that it rests instead of
        // filling. Guessing a price by hand is how a test order becomes a real trade.
        var bandsBody = await GetAsync(http, "Home/GetUpperLowerCap");
        var bands = ParseBands(bandsBody);

        // A missing band is not a curiosity, it is a blocker: the portal's own submit handler compares the
        // price against band globals that getSymbolWiseUpperLoweCap leaves UNCHANGED when it finds no
        // match, so the UI silently reuses the previously-looked-up symbol's band. Record what the
        // endpoint actually covers rather than guessing which symbols it omits.
        Report("BANDS coverage", string.Join("\n", bands.Keys
            .Select(k => k.Split('|')[1]).GroupBy(m => m)
            .Select(g => $"{g.Key}: {g.Count()} symbols")));

        var dump = Environment.GetEnvironmentVariable("AHK_LIVE_BANDS_DUMP");
        if (!string.IsNullOrWhiteSpace(dump))
        {
            try { File.WriteAllText(dump!, bandsBody); Report("BANDS dumped to", dump!); }
            catch (Exception ex) { Report("BANDS dump failed", ex.Message); }
        }
        Report("BANDS for the symbols in this run", string.Join("\n", orders
            .Select(o => o.Symbol.ToUpperInvariant()).Distinct()
            .Select(sym => bands.TryGetValue(BandKey(sym, "REG"), out var b)
                ? $"{sym}: upperCap={b.Upper} lowerLock={b.Lower}"
                : $"{sym}: NO BAND FOUND")));

        try
        {
            foreach (var order in orders)
            {
                var symbol = order.Symbol.ToUpperInvariant();
                var market = order.Market ?? "REG";
                var isBuy  = order.Side.Equals("BUY", StringComparison.OrdinalIgnoreCase);

                var price = order.Price;
                if (string.IsNullOrWhiteSpace(price))
                {
                    Assert.IsTrue(bands.TryGetValue(BandKey(symbol, market), out var band),
                        $"No price band for {symbol} in {market}, so no safe price can be chosen. "
                      + "Give the order an explicit price or pick another symbol.");
                    price = (isBuy ? band.Lower : band.Upper).ToString("F2");
                    Report($"PRICE resolved for {order.Side} {symbol}",
                        $"{price} (the {(isBuy ? "lower lock" : "upper cap")}; it should rest, not fill)");
                }

                var fields = new List<KeyValuePair<string, string>>
                {
                    new("Account",    account),
                    // "BUY" / "SEL" / "SHS" — the portal's own asymmetry, see site.js placeSellOrder.
                    new("BuySell",    isBuy ? "BUY"
                                    : order.Side.Equals("SHS", StringComparison.OrdinalIgnoreCase) ? "SHS" : "SEL"),
                    new("Market",     market),
                    new("OrderType",  order.OrderType ?? "Limit"),
                    new("Volume",     order.Volume.ToString()),
                    new("Script",     symbol),
                    new("Exchange",   "KSE"),
                    new("Price",      price),
                    new("PIN",        config.TradingPin),
                    new("LimitPrice", order.LimitPrice ?? "")
                };

                var sent = string.Join("&", fields
                    .Where(f => f.Key != "PIN")
                    .Select(f => $"{f.Key}={f.Value}"));
                Report($"PLACE {order.Side} {order.Symbol} — request (PIN omitted)", sent);

                var sw = Stopwatch.StartNew();
                using var response = await http.PostAsync("Home/PlaceOrder", new FormUrlEncodedContent(fields));
                var body = await response.Content.ReadAsStringAsync();
                sw.Stop();

                Report($"PLACE {order.Side} {order.Symbol} — response",
                    $"status={(int)response.StatusCode} {response.StatusCode}\n"
                  + $"content-type={response.Content.Headers.ContentType}\n"
                  + $"elapsed={sw.ElapsedMilliseconds}ms\n"
                  + $"length={body.Length}\n"
                  + $"body={body}");

                // The book is the evidence, exactly as on the browser path. Given a moment to appear:
                // an order that fills immediately never rests, so absence here is not proof of failure —
                // which is why the activity log is read too.
                await Task.Delay(3_000);
                Report($"OUTSTANDING after {order.Side} {order.Symbol}",
                    await GetAsync(http, $"Home/GetOutstanding?account={account}"));
                Report($"ACTIVITY LOG after {order.Side} {order.Symbol}",
                    Trim(await GetAsync(http, $"Home/GetActivityLog?account={account}"), 2_000));
            }
        }
        finally
        {
            // Cancel everything resting, whatever happened above. A test that throws mid-way and leaves
            // a live order on a real account would be worse than a test that fails.
            await CancelOrdersThisRunPlacedAsync(http, account, orders, preexisting);
        }
    }

    /// <summary>
    /// Cancels only the orders THIS run placed — resting on a symbol it submitted, and carrying an order
    /// number that was not already in the book beforehand — then reports the book so the result is
    /// verified rather than assumed.
    ///
    /// <para>
    /// The pre-existing set is the whole safety mechanism. This account holds real protective sells, and
    /// "cancel every resting order on the symbols I touched" would remove one the moment a test used the
    /// same symbol. Leaving a test order behind is a phone call; cancelling a stop is an unprotected
    /// position nobody is told about.
    /// </para>
    /// </summary>
    private static async Task CancelOrdersThisRunPlacedAsync(
        HttpClient http, string account, List<OrderSpec> orders, HashSet<string> preexisting)
    {
        var mine = orders.Select(o => o.Symbol.ToUpperInvariant()).ToHashSet();

        var book = await GetAsync(http, $"Home/GetOutstanding?account={account}");
        Report("OUTSTANDING before cancel", book);

        var targets = OrderNumbersFor(book, mine).Where(no => !preexisting.Contains(no)).ToList();
        Report("CANCEL TARGETS", targets.Count == 0 ? "(none — nothing this run placed is resting)" : string.Join(", ", targets));

        foreach (var orderNo in targets)
        {
            // "orignalorderno" is the portal's spelling. Correcting it silently no-ops.
            using var response = await http.PostAsync("Home/CancelOrder",
                new FormUrlEncodedContent([new KeyValuePair<string, string>("orignalorderno", orderNo)]));
            var body = await response.Content.ReadAsStringAsync();
            Report($"CANCEL {orderNo} — response",
                $"status={(int)response.StatusCode}\nlength={body.Length}\nbody={body}");
        }

        await Task.Delay(3_000);
        Report("OUTSTANDING after cancel", await GetAsync(http, $"Home/GetOutstanding?account={account}"));
    }

    /// <summary>
    /// Pulls order numbers out of the outstanding book for the given symbols. Deliberately schema-loose
    /// — this reads whatever the portal returned rather than a typed model, because the point of the run
    /// is to find out what that is.
    /// </summary>
    /// <summary>
    /// Parses <c>GetUpperLowerCap</c> into a symbol+market keyed band table. The response covers the
    /// whole market — roughly 100 KB and every FUT contract too — so it is fetched once per run.
    /// </summary>
    private static Dictionary<string, (decimal Upper, decimal Lower)> ParseBands(string body)
    {
        var bands = new Dictionary<string, (decimal, decimal)>(StringComparer.OrdinalIgnoreCase);

        var json = StripStatus(body);
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                var symbol = row.TryGetProperty("symbol", out var s) ? s.GetString() : null;
                var market = row.TryGetProperty("market", out var m) ? m.GetString() : null;
                if (symbol is null || market is null) continue;
                if (!row.TryGetProperty("upperCap", out var u) || !row.TryGetProperty("lowerLock", out var l)) continue;

                bands[BandKey(symbol, market)] = (u.GetDecimal(), l.GetDecimal());
            }
        }
        catch (Exception ex)
        {
            Report("BANDS parse failed", ex.Message);
        }

        return bands;
    }

    /// <summary>
    /// Removes the <c>"[200] "</c> status marker <see cref="GetAsync"/> prepends, so the remainder can be
    /// parsed as JSON. Every reader of a GetAsync result must go through this: the marker's own bracket
    /// is the first one in the string, so a parser that seeks the first <c>[</c> finds the status code and
    /// then fails — silently, since every reader here is written to degrade rather than throw.
    /// </summary>
    private static string StripStatus(string body) =>
        System.Text.RegularExpressions.Regex.Replace(body ?? "", @"^\[\d{3}\]\s*", "");

    private static string BandKey(string symbol, string market) =>
        $"{symbol.Trim().ToUpperInvariant()}|{market.Trim().ToUpperInvariant()}";

    /// <summary>Every order number in the book, whatever the symbol — the "do not touch" baseline.</summary>
    private static IEnumerable<string> AllOrderNumbers(string book) => OrderNumbersFor(book, null);

    private static IEnumerable<string> OrderNumbersFor(string book, HashSet<string>? symbols)
    {
        if (string.IsNullOrWhiteSpace(book)) yield break;

        JsonDocument doc;
        // StripStatus, not the raw string: GetAsync prefixes "[200] ", and parsing that as JSON fails —
        // which is exactly how a cleanup pass reported "nothing to cancel" while a real order rested.
        try { doc = JsonDocument.Parse(StripStatus(book)); }
        catch { yield break; }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) yield break;

            foreach (var row in doc.RootElement.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) continue;

                string? symbol = null, orderNo = null;
                foreach (var prop in row.EnumerateObject())
                {
                    var name = prop.Name.ToLowerInvariant();
                    var text = prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString()
                        : prop.Value.ToString();

                    if (symbol is null && (name.Contains("symbol") || name.Contains("script") || name.Contains("scrip")))
                        symbol = text?.Trim().ToUpperInvariant();
                    if (orderNo is null && name.Contains("order") && name.Contains("no"))
                        orderNo = text?.Trim();
                }

                if (orderNo is { Length: > 0 } && (symbols is null || (symbol is not null && symbols.Contains(symbol))))
                    yield return orderNo;
            }
        }
    }

    /// <summary>
    /// Drives a complete order cycle through the PRODUCTION code path — `AhkBrowserBrokerAdapter` with
    /// `PreferDirectApiForPlacement` on — and then cancels whatever it placed.
    ///
    /// <para>
    /// The distinction from <see cref="Place_Capture_And_Cancel_Orders"/> matters: that one posts raw HTTP
    /// to learn what the portal does, this one exercises the code that will actually be trading. It covers
    /// the routing decision, the band clamp (the server does not enforce the band, so this path owns it),
    /// the verdict read from the activity log, and the browser fallback when the API refuses.
    /// </para>
    ///
    /// <para>
    /// Safe to run with the market CLOSED, which is the point: an off-hours submission places nothing, so
    /// the whole path can be exercised for real without a trade. Expect the API to refuse and the fallback
    /// to engage — that is a pass, not a failure, and the report says which happened.
    /// </para>
    /// </summary>
    [TestMethod]
    [TestCategory("External")]
    [TestCategory("AhkLiveOrders")]
    public async Task FullCycle_ThroughProductionAdapter()
    {
        var (config, hostConfig) = LoadLiveConfig();

        var spec = Environment.GetEnvironmentVariable("AHK_LIVE_CYCLE");
        if (string.IsNullOrWhiteSpace(spec))
            Assert.Inconclusive("Set AHK_LIVE_CYCLE to one order, e.g. {\"side\":\"SEL\",\"symbol\":\"SYS\",\"volume\":1,\"price\":\"141.65\"}");

        var order = JsonSerializer.Deserialize<OrderSpec>(spec!,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        config.PreferDirectApiForPlacement = true;
        var options = new FixedRuntimeOptions(config);

        await using var broker = new AhkBroker(options, hostConfig, NullLogger<AhkBroker>.Instance);
        using var portal = new AhkPortalClient(broker, options, NullLogger<AhkPortalClient>.Instance);

        // The adapter deliberately refuses to CREATE a session (a periodic caller that logs in would burn a
        // login per pass), so the session is established here, which is the user-initiated path.
        // One HttpClient for the read-backs, from the same browser session the portal client will harvest.
        using var http = await OpenSessionAsync(broker);
        Assert.IsTrue(await portal.EnsureSessionAsync(), "no broker session, so nothing can be exercised");
        Report("SESSION", $"account={portal.AccountCode} hasSession={portal.HasSession}");

        var cancellation = new BrokerOrderCancellationService(
            portal, options, NullLogger<BrokerOrderCancellationService>.Instance);
        var adapter = new AhkBrowserBrokerAdapter(
            broker, portal, cancellation, options, NullLogger<AhkBrowserBrokerAdapter>.Instance);

        var signal = new TradingSignal
        {
            IsSignal = true,
            Action = order.Side.StartsWith("B", StringComparison.OrdinalIgnoreCase) ? "BUY" : "SELL",
            Symbol = order.Symbol,
            Quantity = order.Volume,
            OrderType = string.Equals(order.OrderType, "StopLoss", StringComparison.OrdinalIgnoreCase)
                ? "STOPLOSS" : "LIMIT",
            EntryPrice = decimal.Parse(order.Price ?? "0", CultureInfo.InvariantCulture),
            LimitPrice = order.LimitPrice is null
                ? null : decimal.Parse(order.LimitPrice, CultureInfo.InvariantCulture)
        };

        Report("SIGNAL", $"{signal.Action} {signal.Symbol} x{signal.Quantity} @ {signal.EntryPrice} "
                       + $"type={signal.OrderType} limit={signal.LimitPrice?.ToString() ?? "(none)"}");

        var results = (await adapter.PlaceOrderGroupsAsync(
                new List<IReadOnlyList<TradingSignal>> { new List<TradingSignal> { signal } }.AsReadOnly()))
            .SelectMany(g => g).ToList();

        Assert.AreEqual(1, results.Count, "one signal must produce exactly one result");
        var result = results[0];

        Report("ORDER RESULT",
            $"success={result.Success}\norderId={result.OrderId ?? "(none)"}\n"
          + $"requested={result.RequestedPrice} submitted={result.SubmittedPrice}\n"
          + $"adjustment={result.PriceAdjustment ?? "(none)"}\nmessage={result.Message}");

        try
        {
            Report("OUTSTANDING after", await GetAsync(http, $"Home/GetOutstanding?account={portal.AccountCode}"));
        }
        catch (Exception ex) { Report("OUTSTANDING after", $"(not read: {ex.Message})"); }

        // Whatever landed comes straight back off. Production cancel path, so the cycle is closed by the
        // same code that will close it in service.
        if (!string.IsNullOrWhiteSpace(result.OrderId))
        {
            var cancelled = await portal.CancelOrderAsync(result.OrderId!);
            Report("CANCEL", $"orderNo={result.OrderId} accepted={cancelled}");

            await Task.Delay(3_000);
            Report("OUTSTANDING after cancel",
                await GetAsync(http, $"Home/GetOutstanding?account={portal.AccountCode}"));
        }
        else
        {
            Report("CANCEL", "nothing to cancel — no order number came back, so nothing was placed.");
        }
    }

    /// <summary>
    /// Cancels exactly the order numbers named in <c>AHK_LIVE_CANCEL_ORDERS</c> (comma-separated), then
    /// reports the book. Named orders only — no matching, no inference — because this is the tool you
    /// reach for when something is resting that should not be, and at that moment the last thing wanted
    /// is a heuristic deciding which orders qualify.
    /// </summary>
    [TestMethod]
    [TestCategory("External")]
    [TestCategory("AhkLiveOrders")]
    public async Task Cancel_NamedOrders()
    {
        var list = Environment.GetEnvironmentVariable("AHK_LIVE_CANCEL_ORDERS");
        if (string.IsNullOrWhiteSpace(list))
            Assert.Inconclusive("Set AHK_LIVE_CANCEL_ORDERS to a comma-separated list of order numbers.");

        var wanted = list!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var (config, hostConfig) = LoadLiveConfig();
        await using var broker = new AhkBroker(
            new FixedRuntimeOptions(config), hostConfig, NullLogger<AhkBroker>.Instance);

        using var http = await OpenSessionAsync(broker);
        var account = Account(http);

        Report("OUTSTANDING before cancel", await GetAsync(http, $"Home/GetOutstanding?account={account}"));

        foreach (var orderNo in wanted)
        {
            using var response = await http.PostAsync("Home/CancelOrder",
                new FormUrlEncodedContent([new KeyValuePair<string, string>("orignalorderno", orderNo)]));
            var body = await response.Content.ReadAsStringAsync();
            Report($"CANCEL {orderNo} — response",
                $"status={(int)response.StatusCode}\ncontent-type={response.Content.Headers.ContentType}\n"
              + $"length={body.Length}\nbody={body}");
        }

        await Task.Delay(3_000);
        var after = await GetAsync(http, $"Home/GetOutstanding?account={account}");
        Report("OUTSTANDING after cancel", after);

        var stillResting = AllOrderNumbers(after).Intersect(wanted).ToList();
        Assert.AreEqual(0, stillResting.Count,
            $"Still resting after cancel: {string.Join(", ", stillResting)}");
    }

    // ── Session ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Logs in through <see cref="AhkBroker"/> — which owns the portal's positional-password form — and
    /// returns an <see cref="HttpClient"/> carrying the harvested session cookies.
    /// </summary>
    private static async Task<HttpClient> OpenSessionAsync(AhkBroker broker)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var cookies = await broker.GetSessionCookiesAsync(timeout.Token);
        Assert.IsTrue(cookies.Count > 0, "No session cookies were harvested; the login did not succeed.");

        var baseUri = new Uri("https://web.ahletrade.com/");
        var jar = new CookieContainer();
        foreach (var (name, value, domain) in cookies)
        {
            // Verbatim: the portal's session cookie is already percent-encoded, and re-encoding it makes
            // every later request read as unauthenticated with no error anywhere.
            var host = string.IsNullOrWhiteSpace(domain) ? baseUri.Host : domain.TrimStart('.');
            try { jar.Add(new Cookie(name, value, "/", host)); } catch { /* one bad cookie is not fatal */ }
        }

        var http = new HttpClient(new SocketsHttpHandler
        {
            CookieContainer = jar,
            UseCookies = true,
            // The corporate proxy here demands Negotiate; without this every call fails at the tunnel
            // with 407 while the session still looks established.
            DefaultProxyCredentials = CredentialCache.DefaultCredentials
        })
        {
            BaseAddress = baseUri,
            Timeout = TimeSpan.FromSeconds(30)
        };
        http.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        http.DefaultRequestHeaders.Add("Accept", "*/*");
        http.DefaultRequestHeaders.Referrer = new Uri(baseUri, "Home/Index");

        // The "trader" cookie is the account code every account-scoped endpoint needs.
        var account = cookies.FirstOrDefault(c =>
            string.Equals(c.Name, "trader", StringComparison.OrdinalIgnoreCase)).Value;
        Assert.IsFalse(string.IsNullOrWhiteSpace(account), "No 'trader' cookie, so no account code.");
        _account = account;

        Report("SESSION", $"account={account} cookies={string.Join(", ", cookies.Select(c => c.Name))}");
        return http;
    }

    private static string? _account;
    private static string Account(HttpClient _) => _account ?? throw new InvalidOperationException("No session.");

    private static async Task<string> GetAsync(HttpClient http, string path)
    {
        try
        {
            using var response = await http.GetAsync(path);
            var body = await response.Content.ReadAsStringAsync();
            return $"[{(int)response.StatusCode}] {body}";
        }
        catch (Exception ex) { return $"[EXCEPTION] {ex.Message}"; }
    }

    /// <summary>
    /// Sends one subscription exactly as <c>site.js</c>'s <c>SendSubscription</c> does — jQuery's
    /// <c>formData[i][key]</c> shape, which the portal's model binder reads literally — and returns the
    /// RAW response, because "the POST completed" is not the same claim as "the subscription took".
    /// </summary>
    private static async Task<string> SubscribeAsync(HttpClient http, string page, IEnumerable<string> symbols)
    {
        var market = Environment.GetEnvironmentVariable("AHK_LIVE_MARKET") ?? "REG";
        var fields = new List<KeyValuePair<string, string>>();
        var i = 0;
        foreach (var symbol in symbols)
        {
            fields.Add(new($"formData[{i}][mkt]", market));
            fields.Add(new($"formData[{i}][symbol]", symbol.ToUpperInvariant()));
            i++;
        }
        fields.Add(new("feedtype", "MKT-FEED"));
        fields.Add(new("pagenum", page));

        try
        {
            using var response = await http.PostAsync("Home/SendSubscriptionofSymbols", new FormUrlEncodedContent(fields));
            var body = await response.Content.ReadAsStringAsync();
            return $"[{(int)response.StatusCode}] length={body.Length} body={body}";
        }
        catch (Exception ex) { return $"[EXCEPTION] {ex.Message}"; }
    }

    // ── Config & reporting ────────────────────────────────────────────────────

    /// <summary>
    /// Builds the live <see cref="AhkConfig"/> from the agent's own <c>appsettings.user.json</c>, so no
    /// credential is ever passed on a command line or printed.
    /// </summary>
    private static (AhkConfig Config, IConfiguration HostConfig) LoadLiveConfig()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(EnabledVar), "true", StringComparison.OrdinalIgnoreCase))
            Assert.Inconclusive($"Set {EnabledVar}=true to run this against the live portal.");

        var path = Environment.GetEnvironmentVariable(ConfigVar);
        Assert.IsFalse(string.IsNullOrWhiteSpace(path), $"Set {ConfigVar} to the appsettings.user.json to read.");
        Assert.IsTrue(File.Exists(path), $"{ConfigVar} does not exist: {path}");

        var root = new ConfigurationBuilder()
            .AddJsonFile(path!, optional: false)
            .Build();

        var config = root.GetSection("Plugins:Ahk").Get<AhkConfig>()
            ?? throw new InvalidOperationException("Plugins:Ahk is missing from the config file.");

        Assert.IsFalse(string.IsNullOrWhiteSpace(config.Username), "Plugins:Ahk:Username is empty.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(config.Password), "Plugins:Ahk:Password is empty.");

        // A private profile and log dir: this must never disturb the agent's own session_ahk, and must
        // never be the thing that leaves a half-written profile behind for it to trip over.
        var temp = Path.Combine(Path.GetTempPath(), $"agentfox-ahk-live-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        config.SessionDir = Path.Combine(temp, "session");
        config.LogDir = Path.Combine(temp, "logs");
        config.CloseBrowserAfterOrder = false;   // one session for the whole run
        config.ParkPageAfterCookieHarvest = true; // and no page left polling GetFeed against us

        var hostConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Workspaces:0"] = temp })
            .Build();

        Report("CONFIG", $"account={config.Username} portal={config.PortalUrl} headless={config.Headless} profile={config.SessionDir}");
        return (config, hostConfig);
    }

    /// <summary>
    /// Writes one labelled block to the test output and to <c>AHK_LIVE_REPORT</c> when set. The file
    /// matters: this is evidence to be diffed later, and a console buffer is not where evidence lives.
    /// </summary>
    private static void Report(string label, string content)
    {
        var block = $"===== {label} =====\n{content}\n";
        Console.WriteLine(block);

        var file = Environment.GetEnvironmentVariable("AHK_LIVE_REPORT");
        if (string.IsNullOrWhiteSpace(file)) return;
        try { File.AppendAllText(file, block + "\n"); } catch { /* reporting must not fail the run */ }
    }

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..max] + $"… (+{value.Length - max} more)";

    private sealed class OrderSpec
    {
        public string Side { get; set; } = "";
        public string Symbol { get; set; } = "";
        public int Volume { get; set; }
        public string? Price { get; set; }
        public string? Market { get; set; }
        public string? OrderType { get; set; }
        public string? LimitPrice { get; set; }
    }

    private sealed class FixedRuntimeOptions(AhkConfig config) : IRuntimePluginOptions<AhkConfig>
    {
        public AhkConfig Current => config;
    }
}
