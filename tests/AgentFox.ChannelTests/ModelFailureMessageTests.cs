using System.ClientModel;
using System.ClientModel.Primitives;
using AgentFox.LLM;

namespace AgentFox.ChannelTests;

/// <summary>
/// A model provider refusing its key must not read as the person's own sign-in. The provider text is Kilo's gateway,
/// verbatim, from a hosted agent on 2026-11-26.
/// </summary>
[TestClass]
public sealed class ModelFailureMessageTests
{
    private const string Kilo = "HTTP 401 (: INVALID_TOKEN)\n\nYour authentication token is invalid. Please sign in again.";

    [TestMethod]
    public void A_provider_401_names_the_ai_key_and_keeps_the_providers_words()
    {
        var text = ModelFailureMessage.Describe(new ClientResultException(Kilo, new Response(401)));

        StringAssert.StartsWith(text, "The AI provider rejected its API key (HTTP 401).");
        StringAssert.Contains(text, "not your sign-in to this agent");
        StringAssert.Contains(text, "The provider said: Your authentication token is invalid. Please sign in again.");
    }

    [TestMethod]
    public void A_wrapped_provider_401_is_found()
    {
        var wrapped = new InvalidOperationException("turn failed", new ClientResultException(Kilo, new Response(401)));
        StringAssert.StartsWith(ModelFailureMessage.Describe(wrapped), "The AI provider rejected its API key");
    }

    [TestMethod]
    public void Other_failures_pass_through_unchanged()
    {
        Assert.AreEqual("HTTP 429 (: rate)\n\nslow down",
            ModelFailureMessage.Describe(new ClientResultException("HTTP 429 (: rate)\n\nslow down", new Response(429))));
        // A 401 that is not the model SDK's (a broker, a web tool) is never reworded as an AI-key problem.
        Assert.AreEqual("401 from somewhere else",
            ModelFailureMessage.Describe(new HttpRequestException("401 from somewhere else", null, System.Net.HttpStatusCode.Unauthorized)));
    }

    [TestMethod]
    public void A_401_with_no_provider_text_still_explains_itself()
    {
        var text = ModelFailureMessage.Describe(new ClientResultException("HTTP 401", new Response(401)));
        StringAssert.EndsWith(text, "the provider's key in the AI settings needs replacing.");
    }

    private sealed class Response(int status) : PipelineResponse
    {
        public override int Status => status;
        public override string ReasonPhrase => "";
        protected override PipelineResponseHeaders HeadersCore => new NoHeaders();
        public override Stream? ContentStream { get; set; }
        public override BinaryData Content => BinaryData.FromString("");
        public override BinaryData BufferContent(CancellationToken cancellationToken = default) => Content;
        public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default) => new(Content);
        public override void Dispose() { }
    }

    private sealed class NoHeaders : PipelineResponseHeaders
    {
        public override bool TryGetValue(string name, out string? value) { value = null; return false; }
        public override bool TryGetValues(string name, out IEnumerable<string>? values) { values = null; return false; }
        public override IEnumerator<KeyValuePair<string, string>> GetEnumerator() { yield break; }
    }
}
