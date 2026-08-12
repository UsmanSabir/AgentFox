using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using TradingAgent.Research;

namespace AgentFox.ChannelTests;

/// <summary>
/// The confidence rubric's plumbing. The model's judgement is not testable, but everything around it
/// is — and every one of these behaviours is a way the feature could quietly become dangerous:
/// defaulting to optimism when the model fails, spending a call on a delisted stock, caching a
/// failure for the rest of the session, or letting the model invent a price.
/// </summary>
[TestClass]
public sealed class StockAssessmentServiceTests
{
    [TestMethod]
    public async Task DelistedSecurity_ShortCircuitsToAvoid_WithoutAModelCall()
    {
        var client = new FakeChatClient("""{"confidence":"HIGH","recommendation":"PROCEED"}""");
        var service = Service(client);

        var result = await service.AssessAsync(new StockAssessmentRequest
        {
            Symbol = "DEAD",
            Evidence = new { symbol = "DEAD" },
            IsDelisted = true
        });

        Assert.AreEqual("AVOID", result.Recommendation);
        Assert.AreEqual("NONE", result.Confidence);
        Assert.AreEqual(0, client.Calls,
            "A delisted security is not tradable, so no model call should be spent on it — and no "
            + "favourable answer should be reachable.");
    }

    [TestMethod]
    public async Task AFailedModelCall_FailsConservative()
    {
        var service = Service(new FakeChatClient(new InvalidOperationException("upstream down")));

        var result = await service.AssessAsync(new StockAssessmentRequest
        {
            Symbol = "OGDC",
            Evidence = new { symbol = "OGDC" }
        });

        Assert.AreEqual("INSUFFICIENT_DATA", result.Recommendation);
        Assert.AreEqual("NONE", result.Confidence);
        Assert.AreEqual(0, result.ConfidenceScore,
            "An unavailable assessment must never read as a mild positive.");
    }

    [TestMethod]
    public async Task UnparseableOutput_AlsoFailsConservative()
    {
        var service = Service(new FakeChatClient("I think it looks great, buy it!"));

        var result = await service.AssessAsync(new StockAssessmentRequest
        {
            Symbol = "OGDC",
            Evidence = new { symbol = "OGDC" }
        });

        Assert.AreEqual("INSUFFICIENT_DATA", result.Recommendation);
    }

    [TestMethod]
    public async Task AValidVerdict_IsParsedNormalizedAndClamped()
    {
        var client = new FakeChatClient("""
            Sure! Here you go:
            ```json
            {
              "confidence": "medium",
              "confidence_score": 480,
              "recommendation": " caution ",
              "rationale": "Price is holding the 309 level.",
              "supporting_factors": ["Weekly-confirmed support at 309"],
              "risk_factors": ["Volume below average"],
              "invalidation_level": 309
            }
            ```
            """);

        var result = await Service(client).AssessAsync(new StockAssessmentRequest
        {
            Symbol = "OGDC",
            Evidence = new { symbol = "OGDC" }
        });

        Assert.AreEqual("MEDIUM", result.Confidence, "Casing is normalized.");
        Assert.AreEqual("CAUTION", result.Recommendation, "Whitespace and casing are normalized.");
        Assert.AreEqual(100, result.ConfidenceScore, "An out-of-range score is clamped, not trusted.");
        Assert.AreEqual(309m, result.InvalidationLevel);
        Assert.AreEqual(1, result.SupportingFactors.Count);
        Assert.IsFalse(result.FromCache);
    }

    [TestMethod]
    public async Task RepeatedRequestsForTheSameSituation_AreServedFromCache()
    {
        var client = new FakeChatClient(Verdict("HIGH", "PROCEED"));
        var service = Service(client);
        var request = new StockAssessmentRequest
        {
            Symbol = "OGDC",
            Evidence = new { symbol = "OGDC" },
            CacheKey = StockAssessmentService.CacheKeyFor("OGDC", 309m, "1D")
        };

        var first = await service.AssessAsync(request);
        var second = await service.AssessAsync(request);

        Assert.AreEqual(1, client.Calls, "Clicking twice on one situation must not cost two calls.");
        Assert.IsFalse(first.FromCache);
        Assert.IsTrue(second.FromCache, "A cached verdict says so, rather than posing as fresh.");
    }

    [TestMethod]
    public async Task ADifferentLevel_IsADifferentQuestion()
    {
        var client = new FakeChatClient(Verdict("HIGH", "PROCEED"));
        var service = Service(client);

        await service.AssessAsync(Request("OGDC", 309m));
        await service.AssessAsync(Request("OGDC", 340m));

        Assert.AreEqual(2, client.Calls,
            "A level that has moved is a different setup and deserves a fresh judgement.");
    }

    [TestMethod]
    public async Task AFailedAssessment_IsNotCached()
    {
        var client = new FakeChatClient(new InvalidOperationException("timeout"));
        var service = Service(client);

        await service.AssessAsync(Request("OGDC", 309m));
        await service.AssessAsync(Request("OGDC", 309m));

        Assert.AreEqual(2, client.Calls,
            "Caching a failure would answer 'insufficient data' for the rest of the session because "
            + "one call timed out; the retry must reach the model.");
    }

    [TestMethod]
    public void CacheKey_IsPerSymbolLevelIntervalAndSession()
    {
        Assert.AreEqual(
            StockAssessmentService.CacheKeyFor("ogdc", 309.004m, "1D"),
            StockAssessmentService.CacheKeyFor("OGDC", 309.0m, "1D"),
            "Casing and sub-paisa noise must not split the cache.");

        Assert.AreNotEqual(
            StockAssessmentService.CacheKeyFor("OGDC", 309m, "1D"),
            StockAssessmentService.CacheKeyFor("OGDC", 309m, "15m"),
            "A different timeframe is a different question.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static StockAssessmentService Service(IChatClient client) =>
        new(client, NullLogger<StockAssessmentService>.Instance);

    private static StockAssessmentRequest Request(string symbol, decimal level) => new()
    {
        Symbol = symbol,
        Evidence = new { symbol },
        CacheKey = StockAssessmentService.CacheKeyFor(symbol, level, "1D")
    };

    private static string Verdict(string confidence, string recommendation) =>
        $$"""
        {"confidence":"{{confidence}}","confidence_score":80,"recommendation":"{{recommendation}}",
         "rationale":"ok","supporting_factors":[],"risk_factors":[],"invalidation_level":null}
        """;

    /// <summary>Chat client that returns canned text (or throws), and counts how often it was asked.</summary>
    private sealed class FakeChatClient : IChatClient
    {
        private readonly string? _response;
        private readonly Exception? _failure;

        public FakeChatClient(string response) => _response = response;
        public FakeChatClient(Exception failure) => _failure = failure;

        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            if (_failure is not null) throw _failure;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _response!)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
