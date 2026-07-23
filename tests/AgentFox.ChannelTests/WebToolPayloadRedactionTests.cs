using AgentFox.Modules.Web;

namespace AgentFox.ChannelTests;

[TestClass]
public sealed class WebToolPayloadRedactionTests
{
    [TestMethod]
    public void StructuredPayload_RedactsSecretsRecursively()
    {
        var payload = new Dictionary<string, object?>
        {
            ["query"] = "safe",
            ["nested"] = new Dictionary<string, object?>
            {
                ["api_key"] = "top-secret",
                ["password"] = "also-secret"
            }
        };

        var json = WebModule.RedactToolPayload(payload)!.ToJsonString();

        StringAssert.Contains(json, "\"query\":\"safe\"");
        Assert.IsFalse(json.Contains("top-secret"));
        Assert.IsFalse(json.Contains("also-secret"));
        StringAssert.Contains(json, "[redacted]");
    }

    [TestMethod]
    public void JsonStringResult_IsParsedAndRedacted()
    {
        var json = WebModule.RedactToolPayload(
            """{"answer":4,"authorization":"Bearer private","nested":{"token":"private"}}""")!
            .ToJsonString();

        StringAssert.Contains(json, "\"answer\":4");
        Assert.IsFalse(json.Contains("Bearer private"));
        Assert.IsFalse(json.Contains("\"private\""));
        StringAssert.Contains(json, "[redacted]");
    }

    [TestMethod]
    public void PlainTextResult_IsTruncatedAndMasksCredentialPatterns()
    {
        var input = "Authorization: Bearer-private " + new string('x', 5000);

        var json = WebModule.RedactToolPayload(input)!.ToJsonString();

        Assert.IsFalse(json.Contains("Bearer-private"));
        StringAssert.Contains(json, "[redacted]");
        StringAssert.Contains(json, "[truncated]");
    }
}
