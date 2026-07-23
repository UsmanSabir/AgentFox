using System.Diagnostics;
using AgentFox.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TradingAgent.Broker;
using TradingAgent.Config;

namespace AgentFox.ChannelTests;

/// <summary>
/// Drives <see cref="AhkBroker.GetPortfolioAsync"/> against a LOCAL mock of the AHK Exposure dialog
/// (file://) that reproduces a slow machine: the sidebar handler binds late, the dialog scaffold is
/// built after the click, the ACCOUNT DROPDOWN is populated by a later AJAX call, and the collaterals
/// grid only fills after the Open Position → Collaterals tab flips.
///
/// The account dropdown is the interesting one: the read used to sample it once, see only the
/// "Select Account" placeholder, and return without selecting anything — so no data ever loaded and a
/// healthy account was reported as an empty portfolio with a "could not read balance" warning.
/// </summary>
[TestClass]
public sealed class AhkSlowPortalPortfolioTests
{
    [TestMethod]
    [TestCategory("External")]
    [TestCategory("AhkBrowser")]
    public async Task SlowPortal_ReadsHoldingsAndBalance_WhenTheDialogFillsInStages()
    {
        await using var fixture = await MockPortfolioFixture.CreateAsync();

        var snapshot = await fixture.Broker.GetPortfolioAsync();

        Assert.AreEqual(0, snapshot.Warnings.Count,
            $"Expected a clean read. Warnings: {string.Join(" | ", snapshot.Warnings)}");
        Assert.AreEqual(12_345.67m, snapshot.AvailableBalancePkr);
        Assert.AreEqual(2, snapshot.Holdings.Count);

        var luck = snapshot.Holdings.Single(h => h.Symbol == "LUCK");
        Assert.AreEqual(100m, luck.Quantity);
        Assert.AreEqual(50.00m, luck.AverageBuyPrice);
        Assert.AreEqual(55.00m, luck.CurrentPrice);
        Assert.AreEqual(5_500.00m, luck.CurrentValue);
        Assert.AreEqual(500.00m, luck.ProfitLoss);

        var ogdc = snapshot.Holdings.Single(h => h.Symbol == "OGDC");
        Assert.AreEqual(200m, ogdc.Quantity);
        Assert.AreEqual(-400.00m, ogdc.ProfitLoss);
    }

    /// <summary>
    /// A genuinely empty account renders DataTables' "No data available in table" placeholder. That is
    /// the portal answering, not the portal being slow, so the read must return as soon as it appears
    /// rather than burning the whole PortfolioLoadTimeoutMs waiting for rows that will never come.
    /// </summary>
    [TestMethod]
    [TestCategory("External")]
    [TestCategory("AhkBrowser")]
    public async Task SlowPortal_ReturnsPromptly_WhenTheGridReportsAnEmptyPortfolio()
    {
        await using var fixture = await MockPortfolioFixture.CreateAsync("?empty=1");

        var stopwatch = Stopwatch.StartNew();
        var snapshot  = await fixture.Broker.GetPortfolioAsync();
        stopwatch.Stop();

        Assert.AreEqual(0, snapshot.Holdings.Count);
        Assert.AreEqual(12_345.67m, snapshot.AvailableBalancePkr, "The balance panel still loads normally.");

        // The row poll must not have run to its 10s timeout. Everything before it is ~7s of staged
        // rendering, so 14s leaves generous headroom while still failing if the poll waits it out.
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(14),
            $"Empty-grid read took {stopwatch.Elapsed.TotalSeconds:F1}s — it waited out the row-poll timeout.");
    }

    // ── Fixture ───────────────────────────────────────────────────────────────

    private sealed class MockPortfolioFixture : IAsyncDisposable
    {
        private readonly string _temp;

        public AhkBroker Broker { get; }

        private MockPortfolioFixture(string temp, AhkBroker broker)
        {
            _temp  = temp;
            Broker = broker;
        }

        public static async Task<MockPortfolioFixture> CreateAsync(string query = "")
        {
            var chrome = ResolveChrome();
            if (chrome is null)
            {
                Assert.Inconclusive(
                    "No Chrome/Chromium found. Install Chrome or set AHK_TEST_CHROME_PATH to run this browser test.");
            }

            var temp = Path.Combine(Path.GetTempPath(), $"agentfox-ahk-portfolio-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temp);

            var page = Path.Combine(temp, "mock-ahk-exposure.html");
            await File.WriteAllTextAsync(page, MockPortfolioHtml);

            var config = new AhkConfig
            {
                PortalUrl              = new Uri(page).AbsoluteUri + query,
                Username               = "mock",
                Password               = "mock",
                ExecutablePath         = chrome,
                Headless               = true,
                SessionDir             = Path.Combine(temp, "session"),
                LogDir                 = Path.Combine(temp, "logs"),
                CloseBrowserAfterOrder = true
            };

            var hostConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Workspaces:0"] = temp })
                .Build();

            return new MockPortfolioFixture(temp, new AhkBroker(
                new FixedRuntimeOptions(config), hostConfig, NullLogger<AhkBroker>.Instance));
        }

        public async ValueTask DisposeAsync()
        {
            await Broker.DisposeAsync();
            try { Directory.Delete(_temp, recursive: true); }
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

    // ── Mock Exposure dialog ──────────────────────────────────────────────────
    // Mirrors the AHK portfolio flow the broker encodes: #exposure opens a modal whose scaffold
    // (#expaccount + #collateralstable) is built by the click handler, the account list arrives by a
    // later AJAX call, selecting it loads #exposuretable1 ("Net Cash"), and the collaterals grid only
    // fills after the Open Position → Collaterals tab flips.

    private const string MockPortfolioHtml = """
        <!doctype html>
        <html>
        <head><meta charset="utf-8"><title>Mock AHK Exposure</title></head>
        <body>
          <div id="app">Loading trading screen...</div>
          <script>
            var D = { toolbar: 1500, bind: 2500, scaffold: 800, accounts: 3000, exposure: 1500, tab: 1200, rows: 1500 };
            var EMPTY = new URLSearchParams(location.search).has('empty');
            new URLSearchParams(location.search).forEach(function (v, k) {
              if (k in D) D[k] = parseInt(v, 10);
            });

            // The trading screen (and the sidebar) render after the page reports "loaded".
            setTimeout(function () {
              document.getElementById('app').innerHTML =
                '<button id="buyorder">Buy Order</button>' +
                '<div id="sidebar"><a id="exposure" href="#">Exposure</a></div>';

              // ...and the Exposure handler binds later still: the first dispatched click is swallowed.
              setTimeout(function () {
                document.getElementById('exposure').addEventListener('click', openExposure);
              }, D.bind);
            }, D.toolbar);

            function openExposure() {
              if (document.getElementById('exposuredynamic')) return;
              // The handler builds the scaffold the data AJAX later writes into.
              setTimeout(function () {
                var d = document.createElement('div');
                d.id = 'exposuredynamic';
                d.innerHTML =
                  '<select id="expaccount"><option value="0">Select Account</option></select>' +
                  '<div id="exposuretable1"></div>' +
                  '<a href="#openposition">Open Position</a><a id="collat" href="#collateral">Collaterals</a>' +
                  '<table id="collateralstable"><thead><tr>' +
                    '<th>Symbol</th><th>Total_Qty</th><th>Ave_Rate_Buy</th>' +
                    '<th>MTM_Price</th><th>Amount</th><th>Unsettled</th>' +
                  '</tr></thead><tbody></tbody></table>';
                document.body.appendChild(d);

                // The account list itself arrives by a LATER call — until then only the placeholder.
                setTimeout(function () {
                  var sel = document.getElementById('expaccount');
                  var opt = document.createElement('option');
                  opt.value = 'CC45698';
                  opt.textContent = 'CC45698';
                  sel.appendChild(opt);
                }, D.accounts);

                document.getElementById('expaccount').addEventListener('change', onAccountChange);
                document.querySelector("a[href='#openposition']")
                        .addEventListener('click', function () { window.__openPositionSeen = true; });
                document.getElementById('collat').addEventListener('click', onCollateralsTab);
              }, D.scaffold);
            }

            function onAccountChange() {
              if (this.value === '0') return;
              window.__accountLoaded = true;
              // Exposure summary panels load by AJAX after the change event.
              setTimeout(function () {
                document.getElementById('exposuretable1').innerHTML =
                  '<table><tr><td>Net Cash</td><td>12,345.67</td></tr>' +
                  '<tr><td>Exposure Margin</td><td>0.00</td></tr></table>';
              }, D.exposure);
            }

            function onCollateralsTab() {
              // The grid only fills once the account loaded AND the tabs were flipped — flipping too
              // early (before the account AJAX) must leave it empty, as on the real portal.
              if (!window.__accountLoaded || !window.__openPositionSeen) return;
              setTimeout(function () {
                var body = document.querySelector('#collateralstable tbody');
                if (EMPTY) {
                  body.innerHTML =
                    '<tr class="dataTables_empty"><td colspan="6">No data available in table</td></tr>';
                  return;
                }
                body.innerHTML =
                  '<tr><td>LUCK</td><td>100</td><td>50.00</td><td>55.00</td><td>5,500.00</td><td>500.00</td></tr>' +
                  '<tr><td>OGDC</td><td>200</td><td>90.00</td><td>88.00</td><td>17,600.00</td><td>(400.00)</td></tr>';
              }, D.rows);
            }
          </script>
        </body>
        </html>
        """;
}
