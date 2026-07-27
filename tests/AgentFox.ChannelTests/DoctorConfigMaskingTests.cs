using System.Text.Json.Nodes;
using AgentFox.Doctor;

namespace AgentFox.ChannelTests;

/// <summary>
/// The Doctor edits appsettings.json by handing it to an LLM. That upload must not carry the
/// operator's credentials, and the round trip must not lose them either.
/// </summary>
[TestClass]
public sealed class DoctorConfigMaskingTests
{
    private const string ConfigWithSecrets = """
        {
          // provider settings
          "LLM": {
            "Provider": "Anthropic",
            "ApiKey": "sk-ant-api03-RealOperatorKey00112233",
            "MaxTokens": 4096,
            "BaseUrl": "https://api.anthropic.com"
          },
          "Composio": { "ApiKey": "comp_realkey_998877665544" },
          "Channels": [
            { "Type": "Telegram", "BotToken": "8112233:AAReal-Telegram-Token", "ChatId": "42" }
          ],
          "Tools": { "Shell": true }
        }
        """;

    [TestMethod]
    public void MaskedConfigCarriesNoCredentialToTheModel()
    {
        var masked = DoctorAgent.MaskSecrets(ConfigWithSecrets, out var secrets);

        Assert.IsNotNull(masked);
        Assert.IsFalse(masked!.Contains("sk-ant-api03-RealOperatorKey00112233", StringComparison.Ordinal));
        Assert.IsFalse(masked.Contains("comp_realkey_998877665544", StringComparison.Ordinal));
        Assert.IsFalse(masked.Contains("8112233:AAReal-Telegram-Token", StringComparison.Ordinal));

        // Everything the model actually needs to reason about is still there.
        StringAssert.Contains(masked, "Anthropic");
        StringAssert.Contains(masked, "4096");
        StringAssert.Contains(masked, "https://api.anthropic.com");
        StringAssert.Contains(masked, DoctorAgent.SecretMask);

        CollectionAssert.AreEquivalent(
            new[] { "LLM:ApiKey", "Composio:ApiKey", "Channels:0:BotToken" },
            secrets.Keys.ToArray());
    }

    [TestMethod]
    public void EchoedMasksAreRestoredFromDisk()
    {
        var masked = DoctorAgent.MaskSecrets(ConfigWithSecrets, out var secrets);
        // A cooperative model returns the mask untouched and changes only what was asked.
        var updated = JsonNode.Parse(masked!)!;
        updated["LLM"]!["MaxTokens"] = 8192;

        DoctorAgent.RestoreSecrets(updated, secrets);

        Assert.AreEqual("sk-ant-api03-RealOperatorKey00112233", updated["LLM"]!["ApiKey"]!.GetValue<string>());
        Assert.AreEqual("comp_realkey_998877665544", updated["Composio"]!["ApiKey"]!.GetValue<string>());
        Assert.AreEqual("8112233:AAReal-Telegram-Token",
            updated["Channels"]![0]!["BotToken"]!.GetValue<string>());
        Assert.AreEqual(8192, updated["LLM"]!["MaxTokens"]!.GetValue<int>());
    }

    [TestMethod]
    public void CredentialsSurviveAModelThatDropsThemWhileRewriting()
    {
        DoctorAgent.MaskSecrets(ConfigWithSecrets, out var secrets);
        // The classic failure: the model rewrites the file and simply omits the key.
        var updated = JsonNode.Parse("""
            { "LLM": { "Provider": "Anthropic", "MaxTokens": 4096 }, "Tools": { "Shell": false } }
            """)!;

        DoctorAgent.RestoreSecrets(updated, secrets);

        Assert.AreEqual("sk-ant-api03-RealOperatorKey00112233", updated["LLM"]!["ApiKey"]!.GetValue<string>());
    }

    [TestMethod]
    public void AnExplicitlySuppliedCredentialIsKept()
    {
        var masked = DoctorAgent.MaskSecrets(ConfigWithSecrets, out var secrets);
        var updated = JsonNode.Parse(masked!)!;
        // "set my Composio key to X" — the user supplied it, so the model's value wins.
        updated["Composio"]!["ApiKey"] = "comp_brandnewkey_5566778899";

        DoctorAgent.RestoreSecrets(updated, secrets);

        Assert.AreEqual("comp_brandnewkey_5566778899", updated["Composio"]!["ApiKey"]!.GetValue<string>());
        Assert.AreEqual("sk-ant-api03-RealOperatorKey00112233", updated["LLM"]!["ApiKey"]!.GetValue<string>());
    }

    [TestMethod]
    public void UnparseableConfigIsRefusedRatherThanSentRaw()
    {
        Assert.IsNull(DoctorAgent.MaskSecrets("{ this is not json", out _),
            "a parse failure must not fall back to uploading the raw file");
    }
}
