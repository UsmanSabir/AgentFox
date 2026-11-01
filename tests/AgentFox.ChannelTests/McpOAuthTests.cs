using System.Net;
using System.Net.Sockets;
using AgentFox.MCP;
using AgentFox.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Authentication;

namespace AgentFox.ChannelTests;

/// <summary>
/// OAuth sign-in for HTTP MCP servers: where the tokens are kept and for how long, what the browser's answer is read
/// as, and that startup reports a missing sign-in instead of opening a browser. The protocol itself (discovery,
/// registration, PKCE, refresh) is the SDK's.
/// </summary>
[TestClass]
public sealed class McpOAuthTests
{
    private const string Url = "https://magma.stockji.pk/api/trading/premium/mcp";

    private string _dir = string.Empty;

    [TestInitialize]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mcpoauth_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void TearDown()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private IPluginSecretProtector Protector() =>
        new AesPluginSecretProtector(Path.Combine(_dir, ".key"), NullLogger<AesPluginSecretProtector>.Instance);

    private static TokenContainer Tokens(string access = "access-token-value") => new()
    {
        TokenType = "Bearer",
        AccessToken = access,
        RefreshToken = "refresh-token-value",
        ExpiresIn = 3600,
        ObtainedAt = DateTimeOffset.UtcNow,
        ClientId = "client-1"
    };

    // ── Token store ───────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Tokens_survive_a_restart_and_are_not_readable_on_disk()
    {
        var store = new McpTokenStore(_dir, Protector());
        await store.CacheFor("Stockji", Url).StoreTokensAsync(Tokens(), CancellationToken.None);

        var reopened = new McpTokenStore(_dir, Protector());
        var read = await reopened.CacheFor("Stockji", Url).GetTokensAsync(CancellationToken.None);
        Assert.AreEqual("refresh-token-value", read?.RefreshToken);
        Assert.AreEqual("client-1", read?.ClientId);
        Assert.IsTrue(reopened.Has("stockji", Url), "server names are case-insensitive, as in McpManager");

        foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
            Assert.IsFalse(File.ReadAllText(file).Contains("refresh-token-value"), "tokens are encrypted at rest");
    }

    [TestMethod]
    public void A_server_re_pointed_elsewhere_does_not_get_the_old_servers_token()
    {
        var store = new McpTokenStore(_dir, Protector());
        store.Write("Stockji", Url, Tokens());

        Assert.IsNull(store.Read("Stockji", "https://other.example/mcp"));
        Assert.IsFalse(store.Has("Stockji", "https://other.example/mcp"));
    }

    [TestMethod]
    public void Sweep_keeps_configured_servers_and_deletes_the_rest_and_the_expired()
    {
        var store = new McpTokenStore(_dir, Protector());
        store.Write("kept", Url, Tokens());
        store.Write("unconfigured", Url, Tokens());
        store.Write("old", Url, Tokens());
        var now = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(
            Directory.EnumerateFiles(_dir, McpTokenStore.FileName("old") + ".json").Single(),
            now.AddDays(-McpTokenStore.RetentionDays - 1));

        Assert.AreEqual(2, store.Sweep(["kept", "old"], now));
        Assert.IsTrue(store.Has("kept", Url));
        Assert.IsFalse(store.Has("unconfigured", Url));
        Assert.IsFalse(store.Has("old", Url));
    }

    [TestMethod]
    public void File_names_are_safe_and_two_names_that_clean_up_alike_stay_apart()
    {
        var a = McpTokenStore.FileName("../stock ji");
        var b = McpTokenStore.FileName("../stock_ji");
        Assert.AreNotEqual(a, b);
        Assert.IsFalse(a.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || a.Contains('.') || a.Contains('/'));
    }

    // ── The browser's answer ──────────────────────────────────────────────────

    [TestMethod]
    public void The_redirect_gives_code_state_and_issuer()
    {
        var result = McpLoopbackCallback.Parse(new Uri(
            "http://127.0.0.1:33418/callback?code=abc%2B1&state=s-1&iss=https%3A%2F%2Fstockji.pk"));

        Assert.AreEqual("abc+1", result.Code);
        Assert.AreEqual("s-1", result.State);
        Assert.AreEqual("https://stockji.pk", result.Iss);
    }

    [TestMethod]
    public void A_declined_sign_in_or_an_address_without_a_code_is_refused_in_words()
    {
        var declined = Assert.ThrowsExactly<McpSignInDeclinedException>(() => McpLoopbackCallback.Parse(
            new Uri("http://127.0.0.1:33418/callback?error=access_denied&error_description=The+owner+said+no")));
        Assert.AreEqual("The owner said no", declined.Message);

        Assert.ThrowsExactly<McpSignInDeclinedException>(() =>
            McpLoopbackCallback.Parse(new Uri("http://127.0.0.1:33418/callback?state=s")));
    }

    [TestMethod]
    public async Task The_loopback_listener_ignores_other_paths_and_returns_the_callback()
    {
        var port = FreePort();
        using var callback = McpLoopbackCallback.TryListen(new Uri($"http://127.0.0.1:{port}/callback"), out var problem);
        Assert.IsNotNull(callback, problem);

        var waiting = callback.WaitAsync(CancellationToken.None);
        using var http = new HttpClient();
        var favicon = await http.GetAsync($"http://127.0.0.1:{port}/favicon.ico");
        Assert.AreEqual(HttpStatusCode.NotFound, favicon.StatusCode);
        Assert.IsFalse(waiting.IsCompleted);

        var page = await http.GetAsync($"http://127.0.0.1:{port}/callback?code=c1&state=s1");
        Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
        StringAssert.Contains(await page.Content.ReadAsStringAsync(), "Signed in");

        var result = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("c1", result.Code);
        Assert.AreEqual("s1", result.State);
    }

    [TestMethod]
    public void A_taken_port_is_reported_rather_than_thrown()
    {
        var holder = new TcpListener(IPAddress.Loopback, 0);
        holder.Start();
        try
        {
            var port = ((IPEndPoint)holder.LocalEndpoint).Port;
            using var callback = McpLoopbackCallback.TryListen(new Uri($"http://127.0.0.1:{port}/callback"), out var problem);
            Assert.IsNull(callback);
            StringAssert.Contains(problem, port.ToString());
        }
        finally
        {
            holder.Stop();
        }
    }

    // ── The manager ───────────────────────────────────────────────────────────

    private static McpServerConfig OAuthServer(string name) => new()
    {
        Name = name,
        Transport = new McpTransportConfig
        {
            Type = McpTransportType.Http,
            // Nothing listens on the discard port: a test that reached the network would fail, not hang.
            Url = "http://127.0.0.1:9/mcp",
            OAuth = new McpOAuthConfig { Enabled = true }
        }
    };

    [TestMethod]
    public async Task Startup_with_no_stored_sign_in_reports_it_and_contacts_nothing()
    {
        await using var manager = new McpManager(_dir, Protector());

        Assert.IsFalse(await manager.AddServerAsync(OAuthServer("stockji")));
        StringAssert.Contains(manager.Failures["stockji"], "Not signed in");
        CollectionAssert.AreEqual(new[] { "stockji" }, manager.AwaitingSignIn.ToArray());
        Assert.IsTrue(manager.IsOAuthServer("STOCKJI"));
    }

    [TestMethod]
    public async Task Removing_an_OAuth_server_signs_it_out()
    {
        await using var manager = new McpManager(_dir, Protector());
        var store = new McpTokenStore(_dir, Protector());
        await manager.AddServerAsync(OAuthServer("stockji"));
        store.Write("stockji", "http://127.0.0.1:9/mcp", Tokens());

        await manager.RemoveServerAsync("stockji");

        Assert.IsFalse(store.Has("stockji", "http://127.0.0.1:9/mcp"));
        Assert.AreEqual(0, manager.AwaitingSignIn.Count);
        Assert.IsFalse(manager.Failures.ContainsKey("stockji"));
    }

    [TestMethod]
    public async Task Signing_in_needs_an_OAuth_server_and_completing_needs_a_started_sign_in()
    {
        await using var manager = new McpManager(_dir, Protector());

        var started = await manager.BeginSignInAsync("nothing");
        StringAssert.Contains(started.Error, "not an MCP server configured to sign in with OAuth");

        Assert.IsFalse(manager.CompleteSignIn("nothing", "http://127.0.0.1:33418/callback?code=x", out var error));
        StringAssert.Contains(error, "No sign-in");
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
