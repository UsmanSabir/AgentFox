using System.ClientModel;

namespace AgentFox.LLM;

/// <summary>
/// What a person is told when a turn fails. A model provider's own refusal of its API key reads, verbatim,
/// "HTTP 401 (: INVALID_TOKEN) Your authentication token is invalid. Please sign in again." (Kilo's gateway,
/// 2026-11-26), which sounds like the person's own sign-in to this agent, and signing in again does nothing.
/// So that one case is restated: the provider refused its key, and the key in the AI settings is what to change.
/// Every other failure is passed through unchanged.
/// </summary>
public static class ModelFailureMessage
{
    public static string Describe(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            // ClientResultException comes only from the System.ClientModel SDKs, which here are the model clients
            // (OpenAI, OpenRouter, Kilo and other OpenAI-compatible gateways, Azure OpenAI). A broker or other
            // HTTP 401 never takes this type, so it is never reworded as an AI-key problem.
            if (e is ClientResultException { Status: 401 } refused)
            {
                var said = ProviderText(refused.Message);
                return "The AI provider rejected its API key (HTTP 401). This is not your sign-in to this agent: " +
                       "the provider's key in the AI settings needs replacing." +
                       (said is null ? "" : $" The provider said: {said}");
            }
        }
        return ex.Message;
    }

    // The SDK's message is "HTTP 401 (type: code)" then a blank line then the provider's own text.
    private static string? ProviderText(string message)
    {
        var at = message.IndexOf("\n\n", StringComparison.Ordinal);
        if (at < 0) return null;
        var text = message[(at + 2)..].Trim();
        return text.Length == 0 ? null : text.Length > 300 ? text[..300] + "…" : text;
    }
}
