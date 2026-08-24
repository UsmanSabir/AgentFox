using AgentFox.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TradingAgent.Broker;
using TradingAgent.Config;
using TradingAgent.Models;

namespace AgentFox.ChannelTests;

/// <summary>
/// Drives the real <see cref="AhkBroker"/> against a LOCAL mock portal (file://) that reproduces a slow
/// machine: the toolbar renders late, its click handler binds even later, the modal fades in, the price
/// auto-fill lands seconds after the symbol is typed, the submit button starts disabled, and the
/// "Are you sure?" prompt appears 7s after submit.
///
/// Every one of those delays used to break a real order:
///   • clicking #buyorder before it existed / before its handler bound → dialog never opened;
///   • the exact-text "BUY" submit lookup ran once, while the button was still disabled → order missed;
///   • the confirmation prompt arrived after the old hard-coded 5s window → the order silently never
///     executed and its modal was left blocking the next one.
///
/// No credentials and no live portal are involved — the mock page never talks to a broker.
/// </summary>
[TestClass]
public sealed class AhkSlowPortalOrderTests
{
    private const string Symbol = "LUCK";
    private const int    Qty    = 100;
    private const decimal Price = 55.25m;

    [TestMethod]
    [TestCategory("External")]
    [TestCategory("AhkBrowser")]
    public async Task SlowPortal_PlacesExactlyOneBuy_WhenEveryStepRendersLate()
    {
        await RunSlowPortalOrderAsync(confirmPromptTimeoutMs: 10_000);
    }

    /// <summary>
    /// The dialog stage alone taken to an extreme: the toolbar button appears after 6s and its click
    /// handler binds 3s later, so the FIRST open click is swallowed entirely. The order must still be
    /// placed — the open click is retried until the modal is actually up.
    /// </summary>
    [TestMethod]
    [TestCategory("External")]
    [TestCategory("AhkBrowser")]
    public async Task SlowPortal_OpensDialog_WhenTheFirstOpenClickIsSwallowed()
    {
        await RunSlowPortalOrderAsync(
            confirmPromptTimeoutMs: 10_000,
            delays: "?toolbar=6000&bind=3000&prompt=500");
    }

    /// <summary>
    /// Same slow portal, but the confirmation-prompt budget is deliberately too small (2s vs. the
    /// portal's 7s). The prompt must then be picked up by the outcome poll — an unanswered prompt
    /// means the order never executes, so "gave up waiting" may not be the end of the story.
    /// </summary>
    [TestMethod]
    [TestCategory("External")]
    [TestCategory("AhkBrowser")]
    public async Task SlowPortal_ConfirmsLatePrompt_WhenConfirmTimeoutIsTooSmall()
    {
        await RunSlowPortalOrderAsync(confirmPromptTimeoutMs: 2_000);
    }

    /// <summary>
    /// The live portal leaves lowerCap/upperCap unchanged when a symbol lookup fails. A prior symbol's
    /// band must never be applied to the new symbol. Missing band data is not itself an order veto:
    /// submit the requested price unchanged and let the broker decide.
    /// </summary>
    [TestMethod]
    [TestCategory("External")]
    [TestCategory("AhkBrowser")]
    public async Task MissingPriceBand_DoesNotReuseStaleBand_AndStillSubmitsRequestedPrice()
    {
        await RunSlowPortalOrderAsync(
            confirmPromptTimeoutMs: 2_000,
            delays: "?toolbar=0&bind=0&modal=0&price=100&submit=0&band=missing");
    }

    /// <param name="delays">
    /// Optional query string overriding the mock's per-step delays, e.g. "?toolbar=0&amp;prompt=9000".
    /// </param>
    private static async Task RunSlowPortalOrderAsync(
        int confirmPromptTimeoutMs,
        string delays = "")
    {
        var chrome = ResolveChrome();
        if (chrome is null)
        {
            Assert.Inconclusive(
                "No Chrome/Chromium found. Install Chrome or set AHK_TEST_CHROME_PATH to run this browser test.");
        }

        var temp = Path.Combine(Path.GetTempPath(), $"agentfox-ahk-slow-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);

        try
        {
            var page = Path.Combine(temp, "mock-ahk-portal.html");
            await File.WriteAllTextAsync(page, MockPortalHtml);

            var config = new AhkConfig
            {
                PortalUrl              = new Uri(page).AbsoluteUri + delays,
                Username               = "mock",
                Password               = "mock",
                TradingPin             = "1234",
                ExecutablePath         = chrome,
                Headless               = true,
                SessionDir             = Path.Combine(temp, "session"),
                LogDir                 = Path.Combine(temp, "logs"),
                CloseBrowserAfterOrder = true,
                ConfirmPromptTimeoutMs = confirmPromptTimeoutMs
            };

            var hostConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Workspaces:0"] = temp })
                .Build();

            await using var broker = new AhkBroker(
                new FixedRuntimeOptions(config), hostConfig, NullLogger<AhkBroker>.Instance);

            var result = await broker.PlaceOrderAsync(new TradingSignal
            {
                Action     = "BUY",
                Symbol     = Symbol,
                Quantity   = Qty,
                EntryPrice = Price,
                OrderType  = "LIMIT",
                Confidence = "HIGH"
            });

            Assert.IsTrue(result.Success, $"Order was not confirmed. Message: {result.Message}");
            Assert.AreEqual("998877", result.OrderId);

            // The mock reports what it actually received, so these assert the ORDER, not just the flow:
            // exactly one submit click, and the limit price we typed rather than the portal's late
            // auto-fill (52.10) overwriting it.
            StringAssert.Contains(result.Message, "submits=1",
                $"Submit must run exactly once. Message: {result.Message}");
            StringAssert.Contains(result.Message, $"{Symbol} {Qty} @ 55.25",
                $"The submitted values were wrong. Message: {result.Message}");
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); }
            catch { /* browser teardown can briefly retain profile files on Windows */ }
        }
    }

    private static string? ResolveChrome()
    {
        var configured = Environment.GetEnvironmentVariable("AHK_TEST_CHROME_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

        return new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            }
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe"))
            .FirstOrDefault(File.Exists);
    }

    private sealed class FixedRuntimeOptions(AhkConfig config) : IRuntimePluginOptions<AhkConfig>
    {
        public AhkConfig Current => config;
    }

    // ── Mock portal ───────────────────────────────────────────────────────────
    // Mirrors the AHK DOM the broker depends on (#buyorder toolbar button, the modal's #buy* field ids,
    // an id-less submit button whose only marker is its exact text "BUY", legacy-sweetalert swal-*
    // dialogs) and delays each step far enough to break the pre-fix single-shot lookups.

    private const string MockPortalHtml = """
        <!doctype html>
        <html>
        <head><meta charset="utf-8"><title>Mock AHK Portal</title>
        <style>
          .swal-overlay { position: fixed; inset: 0; background: rgba(0,0,0,.4); }
          .swal-modal { background: #fff; margin: 80px auto; padding: 20px; width: 380px; }
          #dialog { border: 1px solid #ccc; padding: 12px; width: 420px; }
        </style></head>
        <body>
          <div id="app">Loading trading screen...</div>
          <script>
            // Delay profile of a slow machine (ms). Every step is overridable from the query string
            // (?toolbar=0&prompt=7000&...) so a single stage can be slowed down in isolation.
            var D = { toolbar: 3000, bind: 1500, modal: 1200, price: 2000, submit: 1500, prompt: 7000, result: 1500 };
            new URLSearchParams(location.search).forEach(function (v, k) {
              if (k in D) D[k] = parseInt(v, 10);
            });
            window.__submitClicks = 0;
            // The real portal exposes the tradable band as globals, and the broker now waits for them
            // instead of for a price field that never populates on the sell path.
            // Start with a stale band to reproduce the live portal's failed-lookup behaviour. The broker
            // must clear it before typing the requested symbol. Successful mock lookups repopulate it.
            window.lowerCap = 40.00;
            window.upperCap = 60.00;
            window.__resolveBand = new URLSearchParams(location.search).get('band') !== 'missing';

            // 1. The toolbar renders seconds after the page reports "loaded".
            setTimeout(function () {
              document.getElementById('app').innerHTML =
                '<button id="buyorder">Buy Order</button> <button id="sellorder">Sell Order</button>' +
                // The account's own order book: the ONLY place that distinguishes a placed order from
                // one the portal merely claimed to place. Column names mirror the live portal.
                '<a href="#out_log">Outstanding Log</a>' +
                '<div id="out_log"><button>Refresh</button><table><tr>' +
                '<th>Trader</th><th>Market</th><th>Scrip</th><th>Price</th><th>Remaining</th>' +
                '<th>Account</th><th>Order No</th><th>Type</th></tr></table></div>';

              // 2. ...and its click handler binds later still. Clicks before this are swallowed.
              setTimeout(function () {
                document.getElementById('buyorder').addEventListener('click', openBuyDialog);
              }, D.bind);
            }, D.toolbar);

            function openBuyDialog() {
              if (document.getElementById('dialog')) return;
              var d = document.createElement('div');
              d.id = 'dialog';
              d.style.display = 'none';           // 3. the modal fades in
              d.innerHTML =
                '<select id="buyordertype"><option>Market</option><option>Limit</option></select>' +
                '<input id="buysymbol"><input id="buyvolume">' +
                '<input id="buyprice"><input id="buylimitprice"><input id="buyPIN">' +
                '<span>Lower Lock</span><span id="bf-lowerlock">40.00</span>' +
                '<span>Upper Cap</span><span id="bf-uppercap">60.00</span>' +
                '<button id="submitbtn" disabled>BUY</button>';
              document.body.appendChild(d);

              setTimeout(function () { d.style.display = 'block'; }, D.modal);

              // 4. The portal resolves the symbol and overwrites the price field LONG after typing.
              //    Anything typed into #buyprice before this lands is clobbered.
              document.getElementById('buysymbol').addEventListener('input', function () {
                clearTimeout(window.__priceTimer);
                window.__priceTimer = setTimeout(function () {
                  if (window.__resolveBand) {
                    window.lowerCap = 40.00;
                    window.upperCap = 60.00;
                  }
                  document.getElementById('buyprice').value = '52.10';
                }, D.price);
              });

              // 5. The submit button starts disabled.
              setTimeout(function () {
                var b = document.getElementById('submitbtn');
                b.disabled = false;
                b.addEventListener('click', onSubmit);
              }, D.submit);
            }

            function onSubmit() {
              window.__submitClicks++;
              window.__order = {
                symbol: document.getElementById('buysymbol').value,
                volume: document.getElementById('buyvolume').value,
                price:  document.getElementById('buyprice').value
              };
              // 6. The "Are you sure?" prompt appears well after the click. Until OK is pressed the
              //    order does NOT execute.
              setTimeout(showConfirmPrompt, D.prompt);
            }

            function showConfirmPrompt() {
              var o = document.createElement('div');
              o.className = 'swal-overlay';
              o.id = 'confirm-prompt';
              o.innerHTML =
                '<div class="swal-modal"><div class="swal-title">Are you sure?</div>' +
                '<div class="swal-text">You want to execute Buy order!</div>' +
                '<div class="swal-footer">' +
                '<button class="swal-button swal-button--cancel">Cancel</button>' +
                '<button class="swal-button swal-button--confirm">OK</button>' +
                '</div></div>';
              document.body.appendChild(o);
              o.querySelector('.swal-button--cancel').addEventListener('click', function () { o.remove(); });
              o.querySelector('.swal-button--confirm').addEventListener('click', function () {
                o.remove();
                setTimeout(showResultPopup, D.result);
              });
            }

            function bookOrder() {
              var t = document.querySelector('#out_log table');
              if (!t) return;
              var row = t.insertRow(-1);
              ['t1', 'REG', window.__order.symbol, window.__order.price, window.__order.volume,
               'CC00000', '998877', 'Limit'].forEach(function (v) {
                row.insertCell(-1).textContent = v;
              });
            }

            function showResultPopup() {
              bookOrder();
              var r = document.createElement('div');
              r.className = 'swal-overlay';
              r.innerHTML =
                '<div class="swal-modal"><div class="swal-icon swal-icon--success"></div>' +
                '<div class="swal-title">Order Placed</div>' +
                '<div class="swal-text">Order No. 998877 ' +
                  window.__order.symbol + ' ' + window.__order.volume + ' @ ' + window.__order.price +
                  ' (submits=' + window.__submitClicks + ')</div>' +
                '<div class="swal-footer"><button class="swal-button swal-button--confirm">OK</button></div>' +
                '</div>';
              document.body.appendChild(r);
              r.querySelector('.swal-button--confirm').addEventListener('click', function () { r.remove(); });
            }
          </script>
        </body>
        </html>
        """;
}
