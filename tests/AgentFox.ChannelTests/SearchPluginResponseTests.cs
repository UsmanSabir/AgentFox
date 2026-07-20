using System.Net;
using System.Text;
using AgentFox.BraveSearch;
using AgentFox.DuckDuckGoSearch;
using AgentFox.Plugins.Research;
using AgentFox.TavilySearch;
using Microsoft.Extensions.Configuration;

namespace AgentFox.ChannelTests;

[TestClass]
public sealed class SearchPluginResponseTests
{
    [TestMethod]
    public void Tavily_CurrentResponseShape_ParsesSourcedResults()
    {
        const string json = """
            {
              "query": "PSX KSE-30",
              "answer": "The KSE-30 is a PSX index.",
              "results": [
                {
                  "title": "PSX Indices",
                  "url": "https://dps.psx.com.pk/indices",
                  "content": "KSE30 index data",
                  "score": 0.91
                },
                {
                  "title": "Unsafe",
                  "url": "javascript:alert(1)",
                  "content": "discard me",
                  "score": 0.2
                }
              ],
              "request_id": "request-123"
            }
            """;

        var parsed = TavilyWebSearchProvider.ParseResponse(json, "fallback");

        Assert.AreEqual("PSX KSE-30", parsed.Query);
        Assert.AreEqual("request-123", parsed.RequestId);
        Assert.AreEqual(1, parsed.Results.Count);
        Assert.AreEqual("https://dps.psx.com.pk/indices", parsed.Results[0].Url);
    }

    [TestMethod]
    public async Task Tavily_ZeroResults_IsAnExplicitProviderFailure()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Plugins:Tavily:ApiKey"] = "tvly-test-key"
            })
            .Build();
        var http = new HttpClient(new StaticJsonHandler(
            HttpStatusCode.OK,
            """{"query":"simple query","answer":"","results":[],"request_id":"empty-1"}"""));
        var provider = new TavilyWebSearchProvider(configuration, http);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.SearchAsync(new WebSearchRequest("simple query")));

        StringAssert.Contains(ex.Message, "zero sourced results");
        StringAssert.Contains(ex.Message, "empty-1");
    }

    [TestMethod]
    public void Brave_CurrentResponseShape_ParsesWebResults()
    {
        const string json = """
            {
              "query": { "original": "PSX KSE-30" },
              "web": {
                "results": [
                  {
                    "title": "PSX Indices",
                    "url": "https://dps.psx.com.pk/indices",
                    "description": "Official PSX index data",
                    "age": "2026-07-20T10:00:00Z"
                  }
                ]
              }
            }
            """;

        var results = BraveSearchTool.ParseResults(json);

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("PSX Indices", results[0].Title);
        Assert.AreEqual("https://dps.psx.com.pk/indices", results[0].Url);
    }

    [TestMethod]
    public void DuckDuckGo_RelatedTopics_AreFlattenedAndReturned()
    {
        var payload = new DuckResponse
        {
            Heading = "Pakistan Stock Exchange",
            AbstractText = "",
            RelatedTopics =
            [
                new RelatedTopic
                {
                    SubTopics =
                    [
                        new RelatedTopic
                        {
                            Text = "Pakistan Stock Exchange",
                            FirstUrl = "https://duckduckgo.com/Pakistan_Stock_Exchange"
                        }
                    ]
                }
            ]
        };

        var parsed = DuckDuckGoTool.ParseResponse(payload);

        Assert.AreEqual(1, parsed.RelatedTopics.Count);
        Assert.AreEqual("Pakistan Stock Exchange", parsed.RelatedTopics[0].Text);
    }

    private sealed class StaticJsonHandler(HttpStatusCode statusCode, string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }
}
