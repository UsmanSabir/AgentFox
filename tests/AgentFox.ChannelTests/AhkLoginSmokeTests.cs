using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingAgent.Broker;
using TradingAgent.Config;

namespace AgentFox.ChannelTests;

[TestClass]
public sealed class AhkLoginSmokeTests
{
    [TestMethod]
    [TestCategory("External")]
    [TestCategory("AhkLogin")]
    public async Task TestAccount_CanLoginWithoutPlacingAnOrder()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("AHK_TEST_LOGIN_ENABLED"), "true",
                StringComparison.OrdinalIgnoreCase))
        {
            Assert.Inconclusive(
                "Set AHK_TEST_LOGIN_ENABLED=true and the AHK_TEST_* credentials to run this external smoke test.");
        }

        var username = RequiredEnvironmentVariable("AHK_TEST_USERNAME");
        var password = RequiredEnvironmentVariable("AHK_TEST_PASSWORD");
        var temp = Path.Combine(Path.GetTempPath(), $"agentfox-ahk-login-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);

        try
        {
            var config = new AhkConfig
            {
                PortalUrl = Environment.GetEnvironmentVariable("AHK_TEST_PORTAL_URL")
                    ?? "https://web.ahletrade.com/",
                Username = username,
                Password = password,
                TradingPin = Environment.GetEnvironmentVariable("AHK_TEST_TRADING_PIN") ?? "",
                ExecutablePath = Environment.GetEnvironmentVariable("AHK_TEST_CHROME_PATH") ?? "",
                Headless = !string.Equals(Environment.GetEnvironmentVariable("AHK_TEST_HEADLESS"), "false",
                    StringComparison.OrdinalIgnoreCase),
                SessionDir = Path.Combine(temp, "session"),
                LogDir = Path.Combine(temp, "logs"),
                CloseBrowserAfterOrder = true
            };
            var hostConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Workspaces:0"] = temp
                })
                .Build();

            await using var broker = new AhkBroker(
                Options.Create(config), hostConfig, NullLogger<AhkBroker>.Instance);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var result = await broker.VerifyLoginAsync(forceRestart: true, timeout.Token);

            Assert.IsTrue(result.Authenticated);
            Assert.IsFalse(string.IsNullOrWhiteSpace(result.CurrentUrl));
            Assert.AreEqual(config.LoggedInSelector, result.VerifiedSelector);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); }
            catch { /* browser teardown can briefly retain profile files on Windows */ }
        }
    }

    private static string RequiredEnvironmentVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        Assert.IsFalse(string.IsNullOrWhiteSpace(value), $"Environment variable {name} is required.");
        return value!;
    }
}
