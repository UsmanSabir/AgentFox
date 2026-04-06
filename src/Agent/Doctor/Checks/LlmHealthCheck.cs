namespace AgentFox.Doctor.Checks;

using AgentFox.Doctor;
using Microsoft.Extensions.Configuration;
using System.Net.Http;
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
        var baseUrl  = _config["LLM:BaseUrl"] ?? "";

        switch (provider)
        {
            case "ollama":
                await CheckOllamaAsync(baseUrl.Length > 0 ? baseUrl : "http://localhost:11434",
                    model, results, ct);
                break;

            case "openai":
                results.Add(await PingHttpAsync(baseUrl.Length > 0 ? baseUrl : "https://api.openai.com", "OpenAI API", ct));
                break;

            case "anthropic":
                results.Add(await PingHttpAsync("https://api.anthropic.com", "Anthropic API", ct));
                break;

            case "azureopenai":
                if (string.IsNullOrWhiteSpace(baseUrl))
                    results.Add(Warning("AzureOpenAI: LLM:BaseUrl not set, cannot verify endpoint"));
                else
                    results.Add(await PingHttpAsync(baseUrl, "Azure OpenAI endpoint", ct));
                break;

            default:
                results.Add(Warning($"Unknown provider '{provider}' — cannot verify connectivity"));
                break;
        }

        return results;
    }

    private async Task CheckOllamaAsync(string baseUrl, string model,
        List<HealthCheckResult> results, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await http.GetAsync($"{baseUrl.TrimEnd('/')}/api/tags", ct);
            if (!response.IsSuccessStatusCode)
            {
                results.Add(Critical($"Ollama at {baseUrl} returned HTTP {(int)response.StatusCode}"));
                return;
            }

            results.Add(Healthy($"Ollama reachable at {baseUrl}"));

            // Check if the configured model is available
            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var models = doc.RootElement.GetProperty("models");
            bool found = false;
            foreach (var m in models.EnumerateArray())
            {
                var name = m.GetProperty("name").GetString() ?? "";
                if (name.StartsWith(model, StringComparison.OrdinalIgnoreCase))
                { found = true; break; }
            }

            results.Add(found
                ? Healthy($"Model '{model}' is available")
                : Critical($"Model '{model}' not found in Ollama — run: ollama pull {model}",
                    canFix: false));
        }
        catch (Exception ex)
        {
            results.Add(Critical($"Cannot reach Ollama at {baseUrl}: {ex.Message}"));
        }
    }

    private static async Task<HealthCheckResult> PingHttpAsync(
        string url, string label, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            await http.GetAsync(url, ct);
            return Healthy($"{label} reachable");
        }
        catch (Exception ex)
        {
            return Critical($"{label} unreachable: {ex.Message}");
        }
    }

    private static HealthCheckResult Healthy(string msg)  => new(HealthStatus.Healthy,  "LLM Provider", msg);
    private static HealthCheckResult Warning(string msg)  => new(HealthStatus.Warning,  "LLM Provider", msg);
    private static HealthCheckResult Critical(string msg, bool canFix = false, string? fixDesc = null)
        => new(HealthStatus.Critical, "LLM Provider", msg, canFix, fixDesc);
}
