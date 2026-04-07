namespace AgentFox.Doctor.Checks;

using AgentFox.Doctor;
using AgentFox.Http;
using Microsoft.Extensions.Configuration;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

public class LlmHealthCheck : IHealthCheckable
{
    private readonly IConfiguration _config;
    public string ComponentName => "LLM Provider";

    public LlmHealthCheck(IConfiguration config) => _config = config;

    public async Task<IReadOnlyList<HealthCheckResult>> CheckHealthAsync(CancellationToken ct = default)
    {
        var results = new List<HealthCheckResult>();
        var provider = (_config["LLM:Provider"] ?? "").Trim().ToLowerInvariant();
        var model    = _config["LLM:Model"] ?? "";
        var baseUrl  = (_config["LLM:BaseUrl"] ?? "").TrimEnd('/');
        var apiKey   = _config["LLM:ApiKey"]
                       ?? _config["LLM:Headers:Authorization"]?.Replace("Bearer ", "")
                       ?? "";

        switch (provider)
        {
            case "ollama":
                await CheckOllamaAsync(
                    baseUrl.Length > 0 ? baseUrl : "http://localhost:11434",
                    model, results, ct);
                break;

            case "openai":
            case "openrouter":
                await CheckOpenAiCompatibleAsync(
                    baseUrl.Length > 0 ? baseUrl : "https://api.openai.com",
                    apiKey.Length > 0 ? apiKey : (Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? ""),
                    model, provider == "openrouter" ? "OpenRouter" : "OpenAI",
                    results, ct);
                break;

            case "anthropic":
                await CheckAnthropicAsync(
                    apiKey.Length > 0 ? apiKey : (Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? ""),
                    model, results, ct);
                break;

            case "azureopenai":
                if (string.IsNullOrWhiteSpace(baseUrl))
                    results.Add(Warning("AzureOpenAI: LLM:BaseUrl not set, cannot verify endpoint"));
                else
                    await CheckAzureOpenAiAsync(baseUrl, apiKey, model, results, ct);
                break;

            case "googlegenai":
            case "googlevertexai":
                results.Add(Warning($"{provider}: API key verification not supported — configure via Google Cloud credentials"));
                break;

            default:
                results.Add(Warning($"Unknown provider '{provider}' — cannot verify connectivity"));
                break;
        }

        return results;
    }

    // ── Ollama ────────────────────────────────────────────────────────────────

    private async Task CheckOllamaAsync(string baseUrl, string model,
        List<HealthCheckResult> results, CancellationToken ct)
    {
        try
        {
            using var http = HttpResilienceFactory.CreateForHealthCheck(TimeSpan.FromSeconds(5));
            var response = await http.GetAsync($"{baseUrl}/api/tags", ct);
            if (!response.IsSuccessStatusCode)
            {
                results.Add(Critical($"Ollama at {baseUrl} returned HTTP {(int)response.StatusCode}"));
                return;
            }

            results.Add(Healthy($"Ollama reachable at {baseUrl} (no auth required)"));

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var modelsEl = doc.RootElement.GetProperty("models");
            bool found = false;
            foreach (var m in modelsEl.EnumerateArray())
            {
                var name = m.GetProperty("name").GetString() ?? "";
                if (name.StartsWith(model, StringComparison.OrdinalIgnoreCase))
                { found = true; break; }
            }

            results.Add(found
                ? Healthy($"Model '{model}' is available")
                : Critical($"Model '{model}' not found in Ollama — run: ollama pull {model}"));
        }
        catch (Exception ex)
        {
            results.Add(Critical($"Cannot reach Ollama at {baseUrl}: {ex.Message}"));
        }
    }

    // ── OpenAI / OpenRouter ───────────────────────────────────────────────────

    private async Task CheckOpenAiCompatibleAsync(string baseUrl, string apiKey,
        string model, string label, List<HealthCheckResult> results, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            results.Add(Warning($"{label}: No API key configured — set LLM:ApiKey in appsettings.json or OPENAI_API_KEY env var"));
            return;
        }

        try
        {
            using var http = HttpResilienceFactory.CreateForHealthCheck(TimeSpan.FromSeconds(8));
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey);

            var response = await http.GetAsync($"{baseUrl}/v1/models", ct);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                results.Add(Critical($"{label}: API key rejected (401 Unauthorized) — check LLM:ApiKey"));
                return;
            }
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                results.Add(Critical($"{label}: API key forbidden (403) — key may lack permissions"));
                return;
            }
            if (!response.IsSuccessStatusCode)
            {
                results.Add(Warning($"{label}: Unexpected HTTP {(int)response.StatusCode} from {baseUrl}/v1/models"));
                return;
            }

            results.Add(Healthy($"{label}: API key valid, endpoint reachable at {baseUrl}"));

            // Check if configured model appears in the list
            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data))
            {
                bool found = false;
                foreach (var m in data.EnumerateArray())
                {
                    var id = m.TryGetProperty("id", out var idEl) ? (idEl.GetString() ?? "") : "";
                    if (id.Equals(model, StringComparison.OrdinalIgnoreCase))
                    { found = true; break; }
                }
                results.Add(found
                    ? Healthy($"Model '{model}' confirmed available")
                    : Warning($"Model '{model}' not found in {label} model list — check LLM:Model"));
            }
        }
        catch (Exception ex)
        {
            results.Add(Critical($"{label}: Request failed — {ex.Message}"));
        }
    }

    // ── Anthropic ─────────────────────────────────────────────────────────────

    private async Task CheckAnthropicAsync(string apiKey, string model,
        List<HealthCheckResult> results, CancellationToken ct)
    {
        const string BaseUrl = "https://api.anthropic.com";

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            results.Add(Warning("Anthropic: No API key configured — set LLM:ApiKey in appsettings.json or ANTHROPIC_API_KEY env var"));
            return;
        }

        try
        {
            using var http = HttpResilienceFactory.CreateForHealthCheck(TimeSpan.FromSeconds(8));
            http.DefaultRequestHeaders.Add("x-api-key", apiKey);
            http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

            var response = await http.GetAsync($"{BaseUrl}/v1/models", ct);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                results.Add(Critical("Anthropic: API key rejected (401) — check LLM:ApiKey or ANTHROPIC_API_KEY"));
                return;
            }
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                results.Add(Critical("Anthropic: API key forbidden (403) — key may lack permissions"));
                return;
            }
            if (!response.IsSuccessStatusCode)
            {
                results.Add(Warning($"Anthropic: Unexpected HTTP {(int)response.StatusCode}"));
                return;
            }

            results.Add(Healthy("Anthropic: API key valid, endpoint reachable"));

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data))
            {
                bool found = false;
                foreach (var m in data.EnumerateArray())
                {
                    var id = m.TryGetProperty("id", out var idEl) ? (idEl.GetString() ?? "") : "";
                    if (id.Equals(model, StringComparison.OrdinalIgnoreCase) ||
                        id.StartsWith(model, StringComparison.OrdinalIgnoreCase))
                    { found = true; break; }
                }
                results.Add(found
                    ? Healthy($"Model '{model}' confirmed available")
                    : Warning($"Model '{model}' not found in Anthropic model list — check LLM:Model"));
            }
        }
        catch (Exception ex)
        {
            results.Add(Critical($"Anthropic: Request failed — {ex.Message}"));
        }
    }

    // ── Azure OpenAI ──────────────────────────────────────────────────────────

    private async Task CheckAzureOpenAiAsync(string baseUrl, string apiKey, string model,
        List<HealthCheckResult> results, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            results.Add(Warning("AzureOpenAI: No API key configured — set LLM:ApiKey in appsettings.json"));
            return;
        }

        try
        {
            using var http = HttpResilienceFactory.CreateForHealthCheck(TimeSpan.FromSeconds(8));
            http.DefaultRequestHeaders.Add("api-key", apiKey);

            // Azure OpenAI models endpoint
            var url = $"{baseUrl}/openai/models?api-version=2024-02-01";
            var response = await http.GetAsync(url, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                results.Add(Critical("AzureOpenAI: API key rejected (401) — check LLM:ApiKey"));
                return;
            }
            if (!response.IsSuccessStatusCode)
            {
                results.Add(Warning($"AzureOpenAI: HTTP {(int)response.StatusCode} from {baseUrl} — check endpoint and key"));
                return;
            }

            results.Add(Healthy($"AzureOpenAI: API key valid, endpoint reachable at {baseUrl}"));
        }
        catch (Exception ex)
        {
            results.Add(Critical($"AzureOpenAI: Request failed — {ex.Message}"));
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static HealthCheckResult Healthy(string msg)  => new(HealthStatus.Healthy,  "LLM Provider", msg);
    private static HealthCheckResult Warning(string msg)  => new(HealthStatus.Warning,  "LLM Provider", msg);
    private static HealthCheckResult Critical(string msg, bool canFix = false, string? fixDesc = null)
        => new(HealthStatus.Critical, "LLM Provider", msg, canFix, fixDesc);
}
