using AgentFox.Plugins.Research;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Tools;

namespace AgentFox.ChannelTests;

[TestClass]
public sealed class ResearchWebToolTests
{
    [TestMethod]
    public async Task Search_ReturnsBoundedEvidenceAndRegistersSources()
    {
        var provider = new FakeProvider(new WebSearchResponse(
            "PSX KSE-30 latest",
            [
                new WebSearchResult(
                    "Official PSX notice",
                    "https://www.psx.com.pk/notice",
                    new string('x', 300),
                    0.91),
                new WebSearchResult(
                    "Malformed result",
                    "javascript:alert(1)",
                    "must not be returned")
            ],
            "Provider answer",
            "fake",
            DateTime.UtcNow));
        var options = Options.Create(new TradingAgentOptions
        {
            ResearchWebMaxResults = 3,
            ResearchWebMaxContentCharacters = 256
        });
        var tool = new ResearchWebTool(provider, options, NullLogger<ResearchWebTool>.Instance);

        using (ResearchReferenceScope.Begin())
        {
            var result = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["query"] = "PSX KSE-30 latest"
            });

            Assert.IsTrue(result.Success, result.Error);
            StringAssert.Contains(result.Output, "Official PSX notice");
            StringAssert.Contains(result.Output, "\\u2026");
            Assert.IsFalse(result.Output.Contains("javascript:alert(1)", StringComparison.Ordinal));

            var references = ResearchReferenceScope.Current!.Snapshot();
            Assert.AreEqual(1, references.Count);
            Assert.AreEqual("https://www.psx.com.pk/notice", references[0].Url);
            Assert.AreEqual("fake", references[0].Source);
        }

        Assert.AreEqual("PSX KSE-30 latest", provider.LastRequest!.Query);
        Assert.AreEqual(3, provider.LastRequest.MaxResults);
    }

    [TestMethod]
    public async Task Search_RejectsMissingQuery()
    {
        var tool = new ResearchWebTool(
            new FakeProvider(),
            Options.Create(new TradingAgentOptions()),
            NullLogger<ResearchWebTool>.Instance);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>());

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "query");
    }

    [TestMethod]
    public async Task Search_ZeroResults_IsFailureNotEmptySuccess()
    {
        var provider = new FakeProvider(new WebSearchResponse(
            "simple query",
            [],
            null,
            "fake",
            DateTime.UtcNow));
        var tool = new ResearchWebTool(
            provider,
            Options.Create(new TradingAgentOptions()),
            NullLogger<ResearchWebTool>.Instance);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["query"] = "simple query"
        });

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "zero sourced results");
    }

    private sealed class FakeProvider(WebSearchResponse? response = null) : IWebSearchProvider
    {
        private readonly WebSearchResponse _response = response ?? new(
            "",
            [],
            null,
            "fake",
            DateTime.UtcNow);

        public string Name => "fake";
        public WebSearchRequest? LastRequest { get; private set; }

        public Task<WebSearchResponse> SearchAsync(
            WebSearchRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(_response);
        }
    }
}
