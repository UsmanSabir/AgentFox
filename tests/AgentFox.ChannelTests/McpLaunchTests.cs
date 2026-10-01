using AgentFox.MCP;
using AgentFox.Plugins.Security;
using Microsoft.Extensions.Configuration;

namespace AgentFox.ChannelTests;

/// <summary>
/// Covers how a configured MCP server becomes a launchable process on a deployed host: where its
/// executable resolves, which configured entries are in play once settings layers merge, what of
/// the host's environment a bundled server inherits, and that its cache has an enforced expiry.
/// </summary>
[TestClass]
[DoNotParallelize] // mutates the process environment and SecretGuard's static state
public sealed class McpLaunchTests
{
    private const string SecretVar = "AGENTFOX_TEST_MCP_API_KEY";

    private string _base = string.Empty;

    [TestInitialize]
    public void SetUp()
    {
        _base = Path.Combine(Path.GetTempPath(), "mcplaunch_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_base);
        SecretGuard.ResetForTests();
    }

    [TestCleanup]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable(SecretVar, null);
        SecretGuard.ResetForTests();
        try { Directory.Delete(_base, recursive: true); } catch { }
    }

    private static IConfiguration Layers(params Dictionary<string, string?>[] layers)
    {
        var builder = new ConfigurationBuilder();
        foreach (var layer in layers) builder.AddInMemoryCollection(layer);
        return builder.Build();
    }

    private static Dictionary<string, string?> ShippedAnymd() => new()
    {
        ["MCP:BundledServers:anymd:Transport:Type"] = "Stdio",
        ["MCP:BundledServers:anymd:Transport:Command"] = "{workspace}/tools/anymd/anymd",
        ["MCP:BundledServers:anymd:Transport:Arguments:0"] = "mcp",
        ["MCP:BundledServers:anymd:Transport:Arguments:1"] = "--allow-dir={workspace}/documents",
    };

    // ── Command resolution ───────────────────────────────────────────────────

    [TestMethod]
    public void BareCommandNameStaysAPathLookup()
    {
        Assert.AreEqual("npx", McpLaunch.ResolveCommand("npx", _base, isWindows: true, _ => false));
    }

    [TestMethod]
    public void RelativeCommandResolvesAgainstInstallDirectoryNotCwd()
    {
        // Under a Windows service the CWD is System32; the install directory is the only stable anchor.
        var resolved = McpLaunch.ResolveCommand("tools/anymd/anymd", _base, isWindows: false, _ => true);
        Assert.AreEqual(Path.GetFullPath(Path.Combine(_base, "tools", "anymd", "anymd")), resolved);
    }

    [TestMethod]
    public void WorkspaceTokenExpandsToInstallDirectory()
    {
        var resolved = McpLaunch.ResolveCommand("{workspace}/tools/anymd/anymd", _base, isWindows: false, _ => true);
        Assert.AreEqual(Path.GetFullPath(Path.Combine(_base, "tools", "anymd", "anymd")), resolved);
    }

    [TestMethod]
    public void WindowsFallsBackToExeSoOneConfigLineServesEveryPlatform()
    {
        var bare = Path.GetFullPath(Path.Combine(_base, "tools", "anymd", "anymd"));
        Assert.AreEqual(bare + ".exe",
            McpLaunch.ResolveCommand("tools/anymd/anymd", _base, isWindows: true, p => p == bare + ".exe"));
        Assert.AreEqual(bare,
            McpLaunch.ResolveCommand("tools/anymd/anymd", _base, isWindows: false, p => p == bare + ".exe"),
            "Only Windows appends .exe.");
        Assert.AreEqual(bare,
            McpLaunch.ResolveCommand("tools/anymd/anymd", _base, isWindows: true, _ => true),
            "An existing extensionless file is used as named.");
    }

    // ── Which servers are in play ────────────────────────────────────────────

    [TestMethod]
    public void BundledServerStartsWhenItsBinaryShipped()
    {
        var servers = McpLaunch.Compose(Layers(ShippedAnymd()), _base, _ => true, out var notShipped);

        var anymd = servers.Single();
        Assert.AreEqual("anymd", anymd.Name);
        Assert.IsTrue(anymd.Bundled);
        Assert.AreEqual(0, notShipped.Count);
    }

    [TestMethod]
    public void BundledServerWhoseBinaryIsAbsentIsReportedNotStarted()
    {
        var servers = McpLaunch.Compose(Layers(ShippedAnymd()), _base, _ => false, out var notShipped);

        Assert.AreEqual(0, servers.Count);
        CollectionAssert.AreEqual(new[] { "anymd" }, notShipped);
    }

    [TestMethod]
    public void UserDisablesBundledServerByName()
    {
        var user = new Dictionary<string, string?> { ["MCP:BundledServers:anymd:Enabled"] = "false" };

        var servers = McpLaunch.Compose(Layers(ShippedAnymd(), user), _base, _ => true, out _);

        Assert.IsFalse(servers.Single().IsEnabled);
    }

    [TestMethod]
    public void UsersOwnServerListCannotMergeIntoABundledEntry()
    {
        // The failure this layout exists to prevent: arrays merge by index across layers, so a
        // shipped entry at Servers[0] would take the user's Servers[0] fields one by one.
        var user = new Dictionary<string, string?>
        {
            ["MCP:Servers:0:Name"] = "github",
            ["MCP:Servers:0:Transport:Type"] = "Http",
            ["MCP:Servers:0:Transport:Url"] = "https://example.invalid/mcp",
        };

        var servers = McpLaunch.Compose(Layers(ShippedAnymd(), user), _base, _ => true, out _);

        Assert.AreEqual(2, servers.Count);
        var anymd = servers.Single(s => s.Name == "anymd");
        Assert.AreEqual(McpTransportType.Stdio, anymd.Transport!.Type);
        Assert.IsNull(anymd.Transport.Url);
        Assert.AreEqual(McpTransportType.Http, servers.Single(s => s.Name == "github").Transport!.Type);
    }

    [TestMethod]
    public void ServersEntryOfTheSameNameReplacesTheBundledOne()
    {
        var user = new Dictionary<string, string?>
        {
            ["MCP:Servers:0:Name"] = "anymd",
            ["MCP:Servers:0:Transport:Type"] = "Stdio",
            ["MCP:Servers:0:Transport:Command"] = "npx",
        };

        var servers = McpLaunch.Compose(Layers(ShippedAnymd(), user), _base, _ => true, out _);

        var anymd = servers.Single();
        Assert.AreEqual("npx", anymd.Transport!.Command);
        Assert.IsFalse(anymd.Bundled);
    }

    // ── What the launched process gets ───────────────────────────────────────

    [TestMethod]
    public void PrepareExpandsTokensAndCreatesTheFoldersTheServerNeeds()
    {
        var server = new McpServerConfig
        {
            Name = "anymd",
            CreateDirectories = ["{workspace}/documents"],
            Transport = new McpTransportConfig
            {
                Type = McpTransportType.Stdio,
                Command = "{workspace}/tools/anymd/anymd",
                Arguments = ["mcp", "--allow-dir={workspace}/documents"],
                Env = new() { ["ANYMD_CACHE_DIR"] = "{workspace}/cache/anymd" },
            },
        };

        var prepared = McpLaunch.Prepare(server, server.Transport, _base);

        Assert.AreEqual($"--allow-dir={_base}/documents", prepared.Arguments![1]);
        Assert.AreEqual($"{_base}/cache/anymd", prepared.Env!["ANYMD_CACHE_DIR"]);
        Assert.IsTrue(Directory.Exists(Path.Combine(_base, "documents")));
        Assert.AreEqual("{workspace}/tools/anymd/anymd", server.Transport.Command, "The bound config is not mutated.");
    }

    [TestMethod]
    public void BundledServerStartsWithoutTheHostsSecrets()
    {
        Environment.SetEnvironmentVariable(SecretVar, "sk-or-v1-McpLaunchTestKey8817263authQZm");
        var server = new McpServerConfig
        {
            Name = "anymd",
            Bundled = true,
            Transport = new McpTransportConfig
            {
                Type = McpTransportType.Stdio,
                Command = "anymd",
                Env = new() { ["ANYMD_NO_STAR_HINT"] = "1" },
            },
        };

        var env = McpLaunch.Prepare(server, server.Transport, _base).Env!;

        Assert.IsTrue(env.ContainsKey(SecretVar), "The secret must be overlaid as a removal.");
        Assert.IsNull(env[SecretVar]);
        Assert.AreEqual("1", env["ANYMD_NO_STAR_HINT"]);
    }

    [TestMethod]
    public void OperatorConfiguredServerKeepsItsInheritedEnvironment()
    {
        // A server the operator added may read its own key from the environment; stripping it
        // would break that server for a guarantee nobody asked for.
        Environment.SetEnvironmentVariable(SecretVar, "sk-or-v1-McpLaunchTestKey8817263authQZm");
        var server = new McpServerConfig
        {
            Name = "github",
            Transport = new McpTransportConfig { Type = McpTransportType.Stdio, Command = "npx" },
        };

        var env = McpLaunch.Prepare(server, server.Transport, _base).Env;

        Assert.IsTrue(env is null || !env.ContainsKey(SecretVar));
    }

    // ── Cache retention ──────────────────────────────────────────────────────

    [TestMethod]
    public void SweepDeletesOnlyFilesOlderThanRetention()
    {
        var cache = Path.Combine(_base, "cache", "anymd", "images");
        Directory.CreateDirectory(cache);
        var old = Path.Combine(cache, "old.png");
        var fresh = Path.Combine(cache, "fresh.png");
        File.WriteAllText(old, "x");
        File.WriteAllText(fresh, "x");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-8));

        var deleted = McpLaunch.SweepCache(Path.Combine(_base, "cache", "anymd"), 7, DateTime.UtcNow);

        Assert.AreEqual(1, deleted);
        Assert.IsFalse(File.Exists(old));
        Assert.IsTrue(File.Exists(fresh));
    }

    [TestMethod]
    public void SweepWithNoRetentionOrNoFolderDoesNothing()
    {
        Assert.AreEqual(0, McpLaunch.SweepCache(Path.Combine(_base, "missing"), 7, DateTime.UtcNow));
        Assert.AreEqual(0, McpLaunch.SweepCache(_base, 0, DateTime.UtcNow));
    }
}
