using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Research;

namespace AgentFox.ChannelTests;

/// <summary>
/// The portal stopped answering a hosting machine on 2026-10-12 after five agents asked it ~1,500 times an hour,
/// two uncached requests per analysis. These pin that a repeated analysis asks again for nothing, and that a
/// refused series is not asked again on every call.
/// </summary>
[TestClass]
public sealed class PsxSeriesCacheTests
{
    private sealed class FakePortal : IDisposable
    {
        private readonly HttpListener _listener = new();
        public ConcurrentDictionary<string, int> Hits { get; } = new();
        public string BaseUrl { get; }

        public FakePortal()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            BaseUrl = $"http://localhost:{port}/";
            _listener.Prefixes.Add(BaseUrl);
            _listener.Start();
            _ = Task.Run(Serve);
        }

        private async Task Serve()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); } catch { return; }
                var path = ctx.Request.Url!.AbsolutePath;
                Hits.AddOrUpdate(path, 1, (_, n) => n + 1);
                var (status, body) = path switch
                {
                    "/" => (200, """<script>window.__ps = {"_k":"test-key"};</script>"""),
                    _ when path.EndsWith("/REFUSED", StringComparison.Ordinal) => (404, "{}"),
                    "/timeseries/eod/KSE100" => (200, $$"""{"status":1,"data":[[{{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}},165363.11,1000],[{{DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds()}},165966.08,900]]}"""),
                    _ when path.StartsWith("/timeseries/eod/", StringComparison.Ordinal) =>
                        (200, $$"""{"status":1,"data":[[{{DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds()}},101.5,1000],[{{DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeSeconds()}},99,900]]}"""),
                    _ when path.StartsWith("/timeseries/int/", StringComparison.Ordinal) =>
                        (200, $$"""{"status":1,"data":[[{{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}},102,10]]}"""),
                    _ => (404, "{}")
                };
                var bytes = Encoding.UTF8.GetBytes(body);
                ctx.Response.StatusCode = status;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        }

        public int HitsOn(string path) => Hits.GetValueOrDefault(path);
        public void Dispose() { _listener.Stop(); _listener.Close(); }
    }

    private static PsxDataClient Client(FakePortal portal) => new(
        Options.Create(new TradingAgentOptions { PsxDataBaseUrl = portal.BaseUrl }), NullLogger<PsxDataClient>.Instance);

    [TestMethod]
    public async Task Index_daily_change_uses_previous_close_even_when_today_has_an_EOD_row()
    {
        using var portal = new FakePortal();
        var client = Client(portal);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5));
        var close = await client.GetSessionPreviousCloseAsync("KSE100", today);
        Assert.AreEqual(165966.08m, close);
        Assert.AreEqual(-602.97m, 165363.11m - close);
        Assert.AreEqual(-667.95m, 165298.13m - close);
        Assert.AreEqual(close, await client.GetSessionPreviousCloseAsync("KSE100", today));
        Assert.AreEqual(1, portal.HitsOn("/timeseries/eod/KSE100"));
    }

    [TestMethod]
    public async Task A_different_intraday_session_cannot_supply_the_toolbar_baseline()
    {
        using var portal = new FakePortal();
        var client = Client(portal);
        var tomorrow = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5)).AddDays(1);
        Assert.IsNull(await client.GetSessionPreviousCloseAsync("KSE100", tomorrow));
        Assert.AreEqual(0, portal.HitsOn("/timeseries/eod/KSE100"));
        Assert.IsNull(await client.GetSessionPreviousCloseAsync("REFUSED", tomorrow));
    }

    [TestMethod]
    public async Task An_analysis_asks_for_the_end_of_day_series_once_and_never_for_the_intraday_one()
    {
        using var portal = new FakePortal();
        var client = Client(portal);

        for (var i = 0; i < 5; i++)
        {
            var quote = await client.GetQuoteSummaryAsync("OGDC", CancellationToken.None, includeIntraday: false);
            Assert.IsNull(quote.Error);
            Assert.AreEqual(101.5m, quote.High52Week);
        }

        Assert.AreEqual(1, portal.HitsOn("/timeseries/eod/OGDC"));
        Assert.AreEqual(0, portal.HitsOn("/timeseries/int/OGDC"));
    }

    [TestMethod]
    public async Task Concurrent_callers_share_one_request()
    {
        using var portal = new FakePortal();
        var client = Client(portal);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.GetQuoteSummaryAsync("PPL")));

        Assert.AreEqual(1, portal.HitsOn("/timeseries/eod/PPL"));
        Assert.AreEqual(1, portal.HitsOn("/timeseries/int/PPL"));
    }

    [TestMethod]
    public async Task A_refused_series_is_not_asked_again_on_every_call_and_says_so()
    {
        using var portal = new FakePortal();
        var client = Client(portal);

        var first = await client.GetQuoteSummaryAsync("REFUSED", CancellationToken.None, includeIntraday: false);
        var second = await client.GetQuoteSummaryAsync("REFUSED", CancellationToken.None, includeIntraday: false);

        Assert.IsNotNull(first.Error);
        StringAssert.Contains(second.Error, "asked again after");
        Assert.AreEqual(1, portal.HitsOn("/timeseries/eod/REFUSED"));
    }
}
