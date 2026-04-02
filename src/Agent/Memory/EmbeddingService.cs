using Microsoft.Extensions.Configuration;
using OllamaSharp;
using OllamaSharp.Models;
using OpenAI;
using OpenAI.Embeddings;
using System.ClientModel;
using System.Diagnostics;

namespace AgentFox.Memory;

/// <summary>
/// Generates a dense vector embedding for a text string.
/// Returns null when the provider is unavailable or the call fails.
/// </summary>
public interface IEmbeddingService
{
    Task<float[]?> GenerateAsync(string text, CancellationToken ct = default);
}

/// <summary>No-op implementation — vector search is disabled.</summary>
public sealed class NullEmbeddingService : IEmbeddingService
{
    public Task<float[]?> GenerateAsync(string text, CancellationToken ct = default)
        => Task.FromResult<float[]?>(null);
}

/// <summary>Ollama-backed embeddings via OllamaSharp.</summary>
public sealed class OllamaEmbeddingService : IEmbeddingService
{
    private readonly OllamaApiClient _client;
    private readonly string _model;

    public OllamaEmbeddingService(string baseUrl, string model)
    {
        _model = model;
        _client = new OllamaApiClient(new OllamaApiClient.Configuration
        {
            Uri   = new Uri(baseUrl),
            Model = model
        });
    }

    public async Task<float[]?> GenerateAsync(string text, CancellationToken ct = default)
    {
        try
        {
            var response = await _client.EmbedAsync(
                new EmbedRequest { Model = _model, Input = [text] }, ct);
            return response?.Embeddings?[0];
        }
        catch { return null; }
    }
}

/// <summary>OpenAI-backed embeddings (also works with compatible endpoints).</summary>
public sealed class OpenAIEmbeddingService : IEmbeddingService
{
    private readonly EmbeddingClient _client;

    public OpenAIEmbeddingService(string apiKey, string model,
        string baseUrl = "https://api.openai.com/v1")
    {
        var options = new OpenAIClientOptions { Endpoint = new Uri(baseUrl) };
        var openAiClient = new OpenAIClient(new ApiKeyCredential(apiKey), options);
        _client = openAiClient.GetEmbeddingClient(model);
    }

    public async Task<float[]?> GenerateAsync(string text, CancellationToken ct = default)
    {
        try
        {
            var result = await _client.GenerateEmbeddingAsync(text, cancellationToken: ct);
            return result.Value.ToFloats().ToArray();
        }
        catch(Exception ex) 
        { 
            if(Debugger.IsAttached)
                Debugger.Break();
            return null; }
    }
}

/// <summary>Configuration for the embedding provider (nested under "Memory:Embedding").</summary>
public class EmbeddingConfig
{
    /// <summary>"Ollama", "OpenAI", or "None" (default).</summary>
    public string Provider { get; set; } = "None";

    /// <summary>Model name — e.g. "nomic-embed-text" for Ollama, "text-embedding-3-small" for OpenAI.</summary>
    public string Model { get; set; } = "nomic-embed-text";

    /// <summary>Base URL for the embedding endpoint.</summary>
    public string BaseUrl { get; set; } = "http://localhost:11434";

    /// <summary>Optional API key (falls back to OPENAI_API_KEY env var for OpenAI).</summary>
    public string? ApiKey { get; set; }
}

public static class EmbeddingServiceFactory
{
    public static IEmbeddingService Create(IConfiguration configuration)
    {
        // Resolve config: prefer a named model from Models:{ModelRef}, fall back to Memory:Embedding.
        var modelRef = configuration["Memory:ModelRef"];
        EmbeddingConfig config;
        if (!string.IsNullOrWhiteSpace(modelRef))
            config = configuration.GetSection($"Models:{modelRef}").Get<EmbeddingConfig>() ?? new EmbeddingConfig();
        else
            config = configuration.GetSection("Memory:Embedding").Get<EmbeddingConfig>() ?? new EmbeddingConfig();

        return config.Provider.Trim().ToLowerInvariant() switch
        {
            "ollama" => new OllamaEmbeddingService(config.BaseUrl, config.Model),
            "openai" => new OpenAIEmbeddingService(
                config.ApiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty,
                config.Model,
                config.BaseUrl),
            _ => new NullEmbeddingService()
        };
    }
}
