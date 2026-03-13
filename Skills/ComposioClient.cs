using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AgentFox.Skills;

/// <summary>
/// Composio.dev API client for discovering and managing integrations
/// </summary>
public class ComposioClient
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly ILogger<ComposioClient>? _logger;
    //private const string DefaultBaseUrl = "https://api.composio.dev/api/v1";
    private const string DefaultBaseUrl = "https://backend.composio.dev/api/v3/";
    //https://docs.composio.dev/reference

    public ComposioClient(string apiKey, ILogger<ComposioClient>? logger = null, string? baseUrl = null)
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _baseUrl = baseUrl ?? DefaultBaseUrl;
        _logger = logger;
        _httpClient = new HttpClient();
        //_httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        _httpClient.DefaultRequestHeaders.Add("x-api-key", apiKey);
    }

    /// <summary>
    /// Get all available integrations from Composio.dev
    /// </summary>
    public async Task<List<ComposioIntegration>> GetIntegrationsAsync()
    {
        try
        {
            //var response = await _httpClient.GetAsync($"{_baseUrl}/integrations");
            var response = await _httpClient.GetAsync($"{_baseUrl}/tools");
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var result = JsonSerializer.Deserialize<ComposioApiResponse<ComposioIntegration>>(content, options);
            
            _logger?.LogInformation("Retrieved {Count} integrations from Composio.dev", result?.Data?.Count ?? 0);
            return result?.Data ?? new();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to get integrations from Composio.dev");
            throw;
        }
    }

    /// <summary>
    /// Get all available actions for a specific integration
    /// </summary>
    public async Task<List<ComposioAction>> GetActionsAsync(string integrationId)
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_baseUrl}/integrations/{integrationId}/actions");
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var result = JsonSerializer.Deserialize<ComposioApiResponse<ComposioAction>>(content, options);
            
            _logger?.LogInformation("Retrieved {Count} actions for integration {IntegrationId}", 
                result?.Data?.Count ?? 0, integrationId);
            return result?.Data ?? new();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to get actions for integration {IntegrationId}", integrationId);
            throw;
        }
    }

    /// <summary>
    /// Execute an action through Composio.dev
    /// </summary>
    public async Task<Dictionary<string, object>> ExecuteActionAsync(
        string integrationId, 
        string actionId, 
        Dictionary<string, object> parameters)
    {
        try
        {
            var payload = new
            {
                integrationId,
                actionId,
                input = parameters
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync($"{_baseUrl}/actions/execute", content);
            
            response.EnsureSuccessStatusCode();
            var responseContent = await response.Content.ReadAsStringAsync();
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var result = JsonSerializer.Deserialize<Dictionary<string, object>>(responseContent, options);
            
            _logger?.LogInformation("Executed action {ActionId} on integration {IntegrationId}", 
                actionId, integrationId);
            return result ?? new();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to execute action {ActionId} on integration {IntegrationId}", 
                actionId, integrationId);
            throw;
        }
    }

    /// <summary>
    /// Get detailed information about an integration
    /// </summary>
    public async Task<ComposioIntegration?> GetIntegrationAsync(string integrationId)
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_baseUrl}/integrations/{integrationId}");
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var result = JsonSerializer.Deserialize<ComposioApiResponse<ComposioIntegration>>(content, options);
            
            return result?.Data?.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to get integration details for {IntegrationId}", integrationId);
            throw;
        }
    }

    /// <summary>
    /// Get available auth modes for an integration
    /// </summary>
    public async Task<List<string>> GetAuthModesAsync(string integrationId)
    {
        try
        {
            var integration = await GetIntegrationAsync(integrationId);
            return integration?.AuthModes ?? new();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to get auth modes for integration {IntegrationId}", integrationId);
            throw;
        }
    }
}

/// <summary>
/// Represents a Composio.dev integration
/// </summary>
public class ComposioIntegration
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("logo")]
    public string? Logo { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("auth_modes")]
    public List<string> AuthModes { get; set; } = new();

    [JsonPropertyName("is_available")]
    public bool IsAvailable { get; set; } = true;

    [JsonPropertyName("actions_count")]
    public int ActionsCount { get; set; }
}

/// <summary>
/// Represents a Composio.dev action
/// </summary>
public class ComposioAction
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("input_params")]
    public List<ComposioParameter> InputParams { get; set; } = new();

    [JsonPropertyName("output_params")]
    public List<ComposioParameter> OutputParams { get; set; } = new();

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();
}

/// <summary>
/// Represents a parameter for a Composio action
/// </summary>
public class ComposioParameter
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "string";

    [JsonPropertyName("required")]
    public bool Required { get; set; }

    [JsonPropertyName("default")]
    public object? Default { get; set; }

    [JsonPropertyName("enum")]
    public List<string> EnumValues { get; set; } = new();
}

/// <summary>
/// Generic API response wrapper for Composio.dev
/// </summary>
/// <typeparam name="T">Response data type</typeparam>
public class ComposioApiResponse<T>
{
    [JsonPropertyName("data")]
    public List<T> Data { get; set; } = new();

    [JsonPropertyName("success")]
    public bool Success { get; set; } = true;

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
