using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using AgentFox.Http;
using Microsoft.Extensions.Logging;

namespace AgentFox.Skills;

/// <summary>
/// Composio.dev API client for discovering and managing toolkits and tools
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
        _httpClient = HttpResilienceFactory.Create(TimeSpan.FromSeconds(60));
        //_httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        _httpClient.DefaultRequestHeaders.Add("x-api-key", apiKey);
    }

    /// <summary>
    /// Get all available toolkits from Composio.dev
    /// </summary>
    public async Task<List<ComposioToolkit>> GetToolkitsAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_baseUrl}toolkits");
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            
            // Try items-based response first, fall back to data-based
            ComposioApiResponseWithItems<ComposioToolkit>? result;
            try
            {
                result = JsonSerializer.Deserialize<ComposioApiResponseWithItems<ComposioToolkit>>(content, options);
            }
            catch
            {
                var dataResult = JsonSerializer.Deserialize<ComposioApiResponse<ComposioToolkit>>(content, options);
                result = new ComposioApiResponseWithItems<ComposioToolkit> { Items = dataResult?.Data ?? new() };
            }
            
            _logger?.LogInformation("Retrieved {Count} toolkits from Composio.dev", result?.Items?.Count ?? 0);
            return result?.Items ?? new();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to get toolkits from Composio.dev");
            throw;
        }
    }

    /// <summary>
    /// Get all available tools for a specific toolkit by slug
    /// </summary>
    public async Task<List<ComposioTool>> GetToolsAsync(string toolkitSlug)
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_baseUrl}tools?toolkit_slug={toolkitSlug}");
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var result = JsonSerializer.Deserialize<ComposioApiResponseWithItems<ComposioTool>>(content, options);
            
            _logger?.LogInformation("Retrieved {Count} tools for toolkit {ToolkitSlug}", 
                result?.Items?.Count ?? 0, toolkitSlug);
            return result?.Items ?? new();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to get tools for toolkit {ToolkitSlug}", toolkitSlug);
            throw;
        }
    }

    /// <summary>
    /// Get all authentication configurations for authorized toolkits
    /// </summary>
    public async Task<List<ComposioAuthConfig>> GetAuthConfigsAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_baseUrl}auth_configs");
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var result = JsonSerializer.Deserialize<ComposioAuthConfigResponse>(content, options);
            
            _logger?.LogInformation("Retrieved {Count} auth configs", result?.Items?.Count ?? 0);
            return result?.Items ?? new();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to get auth configs");
            throw;
        }
    }

    /// <summary>
    /// Get all connected accounts
    /// </summary>
    public async Task<List<ComposioConnectedAccount>> GetConnectedAccountsAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_baseUrl}connected_accounts");
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var result = JsonSerializer.Deserialize<ComposioConnectedAccountsResponse>(content, options);
            
            _logger?.LogInformation("Retrieved {Count} connected accounts", result?.Items?.Count ?? 0);
            return result?.Items ?? new();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to get connected accounts");
            throw;
        }
    }

    /// <summary>
    /// Get active connected accounts for a specific toolkit
    /// </summary>
    public async Task<List<ComposioConnectedAccount>> GetActiveConnectedAccountsAsync()
    {
        try
        {
            var allAccounts = await GetConnectedAccountsAsync();
            var activeAccounts = allAccounts
                .Where(a => a.Status == "ACTIVE")
                .ToList();
            
            _logger?.LogInformation("Found {Count} active connected accounts", activeAccounts.Count);
            return activeAccounts;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to get active connected accounts");
            throw;
        }
    }

    /// <summary>
    /// Get active connected accounts for a specific toolkit
    /// </summary>
    public async Task<List<ComposioConnectedAccount>> GetActiveConnectedAccountsAsync(string toolkitSlug)
    {
        try
        {
            var allAccounts = await GetActiveConnectedAccountsAsync();
            var toolkitAccounts = allAccounts
                .Where(a => a.Toolkit?.Slug == toolkitSlug)
                .ToList();
            
            _logger?.LogInformation("Found {Count} active connected accounts for toolkit {ToolkitSlug}", 
                toolkitAccounts.Count, toolkitSlug);
            return toolkitAccounts;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to get active connected accounts for toolkit {ToolkitSlug}", toolkitSlug);
            throw;
        }
    }

    /// <summary>
    /// Get all enabled toolkits from auth configs
    /// </summary>
    public async Task<List<ComposioToolkit>> GetEnabledToolkitsAsync()
    {
        try
        {
            var authConfigs = await GetAuthConfigsAsync();
            var toolkits = new Dictionary<string, ComposioToolkit>();
            
            foreach (var config in authConfigs.Where(c => c.Status == "ENABLED"))
            {
                if (config.Toolkit != null && !string.IsNullOrEmpty(config.Toolkit.Slug))
                {
                    if (!toolkits.ContainsKey(config.Toolkit.Slug))
                    {
                        toolkits[config.Toolkit.Slug] = new ComposioToolkit
                        {
                            Id = config.Toolkit.Slug,
                            Name = config.Toolkit.Slug,
                            Slug = config.Toolkit.Slug,
                            IsAvailable = true,
                            Logo = config.Toolkit.Logo
                        };
                    }
                }
            }
            
            _logger?.LogInformation("Found {Count} enabled toolkits from auth configs", toolkits.Count);
            return toolkits.Values.ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to get enabled toolkits");
            throw;
        }
    }

    /// <summary>
    /// Execute a tool through Composio.dev using a specific connected account
    /// </summary>
    public async Task<Dictionary<string, object>> ExecuteToolAsync(
        string toolSlug,
        Dictionary<string, object> parameters,
        string connectedAccountId,
        string userId)
    {
        try
        {
            var payload = new
            {
                connected_account_id = connectedAccountId,
                user_id = userId,
                arguments = parameters
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync($"{_baseUrl}tools/execute/{toolSlug}", content);
            
            response.EnsureSuccessStatusCode();
            var responseContent = await response.Content.ReadAsStringAsync();
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var result = JsonSerializer.Deserialize<ComposioExecutionResponse>(responseContent, options);
            
            _logger?.LogInformation("Executed tool {ToolSlug} with account {ConnectedAccountId}", 
                toolSlug, connectedAccountId);
            return result?.Data ?? new();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to execute tool {ToolSlug} with account {ConnectedAccountId}", 
                toolSlug, connectedAccountId);
            throw;
        }
    }

    /// <summary>
    /// Get a specific tool by slug
    /// </summary>
    public async Task<ComposioTool?> GetToolAsync(string toolSlug)
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_baseUrl}tools/{toolSlug}");
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var result = JsonSerializer.Deserialize<ComposioApiResponse<ComposioTool>>(content, options);
            
            return result?.Data?.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to get tool details for {ToolSlug}", toolSlug);
            throw;
        }
    }

    /// <summary>
    /// Get detailed information about a toolkit
    /// </summary>
    public async Task<ComposioToolkit?> GetToolkitAsync(string toolkitSlug)
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_baseUrl}toolkits/{toolkitSlug}");
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var result = JsonSerializer.Deserialize<ComposioApiResponse<ComposioToolkit>>(content, options);
            
            return result?.Data?.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to get toolkit details for {ToolkitSlug}", toolkitSlug);
            throw;
        }
    }

    /// <summary>
    /// Get available auth modes for a toolkit
    /// </summary>
    public async Task<List<string>> GetAuthModesAsync(string toolkitSlug)
    {
        try
        {
            var toolkit = await GetToolkitAsync(toolkitSlug);
            return toolkit?.AuthModes ?? new();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to get auth modes for toolkit {ToolkitSlug}", toolkitSlug);
            throw;
        }
    }
}

/// <summary>
/// Represents a Composio.dev toolkit
/// </summary>
public class ComposioToolkit
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

    [JsonPropertyName("tools_count")]
    public int ToolsCount { get; set; }

    [JsonPropertyName("slug")]
    public string? Slug { get; set; }
    
    public string? ConnectedAccountId { get; set; }

    [JsonPropertyName("user_id")]
    public string? UserId { get; set; }
}

/// <summary>
/// Represents a Composio.dev tool (action)
/// </summary>
public class ComposioTool
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("slug")]
    public string Slug { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("human_description")]
    public string? HumanDescription { get; set; }

    [JsonPropertyName("input_parameters")]
    public ComposioParameterSchema? InputParameters { get; set; }

    [JsonPropertyName("output_parameters")]
    public ComposioParameterSchema? OutputParameters { get; set; }

    [JsonPropertyName("scopes")]
    public List<string> Scopes { get; set; } = new();

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();

    [JsonPropertyName("no_auth")]
    public bool NoAuth { get; set; }

    [JsonPropertyName("is_deprecated")]
    public bool IsDeprecated { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    // Keep old properties for backward compatibility
    [JsonIgnore]
    public List<ComposioParameter> InputParams
    {
        get
        {
            if (InputParameters?.Properties == null)
                return new();

            var result = new List<ComposioParameter>();
            foreach (var prop in InputParameters.Properties)
            {
                result.Add(new ComposioParameter
                {
                    Name = prop.Key,
                    Description = prop.Value.Description ?? string.Empty,
                    Type = prop.Value.Type ?? "string",
                    Required = InputParameters.Required?.Contains(prop.Key) ?? false,
                    Default = prop.Value.Default,
                    EnumValues = prop.Value.EnumValues?.Select(e => e?.ToString() ?? "").ToList() ?? new()
                });
            }
            return result;
        }
    }

    [JsonIgnore]
    public List<ComposioParameter> OutputParams
    {
        get
        {
            if (OutputParameters?.Properties == null)
                return new();

            var result = new List<ComposioParameter>();
            foreach (var prop in OutputParameters.Properties)
            {
                result.Add(new ComposioParameter
                {
                    Name = prop.Key,
                    Description = prop.Value.Description ?? string.Empty,
                    Type = prop.Value.Type ?? "string",
                    Required = OutputParameters.Required?.Contains(prop.Key) ?? false,
                    Default = prop.Value.Default,
                    EnumValues = prop.Value.EnumValues?.Select(e => e?.ToString() ?? "").ToList() ?? new()
                });
            }
            return result;
        }
    }
}

/// <summary>
/// Represents a JSON Schema definition for tool parameters
/// </summary>
public class ComposioParameterSchema
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("required")]
    public List<string> Required { get; set; } = new();

    [JsonPropertyName("properties")]
    public Dictionary<string, ComposioPropertyDefinition>? Properties { get; set; }

    [JsonPropertyName("additionalProperties")]
    public object? AdditionalProperties { get; set; }
}

/// <summary>
/// Represents a property definition within a JSON Schema
/// </summary>
public class ComposioPropertyDefinition
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("default")]
    public object? Default { get; set; }

    [JsonPropertyName("enum")]
    public List<object>? EnumValues { get; set; }

    [JsonPropertyName("items")]
    public Dictionary<string, object>? Items { get; set; }

    [JsonPropertyName("properties")]
    public Dictionary<string, ComposioPropertyDefinition>? Properties { get; set; }

    [JsonPropertyName("required")]
    public List<string>? RequiredFields { get; set; }

    [JsonExtensionData]
    public Dictionary<string, object>? AdditionalData { get; set; }
}

/// <summary>
/// Represents a parameter definition for a Composio tool (with detailed schema)
/// </summary>
public class ComposioToolParameter
{
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("required")]
    public bool? Required { get; set; }

    [JsonPropertyName("default")]
    public object? Default { get; set; }

    [JsonPropertyName("enum")]
    public List<string>? EnumValues { get; set; }

    [JsonPropertyName("schema")]
    public Dictionary<string, object>? Schema { get; set; }
}

/// <summary>
/// Represents a parameter for a Composio action (legacy format)
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
/// Represents a toolkit reference in auth config
/// </summary>
public class ComposioToolkitReference
{
    [JsonPropertyName("slug")]
    public string Slug { get; set; } = string.Empty;

    [JsonPropertyName("logo")]
    public string? Logo { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

/// <summary>
/// Represents an authentication configuration for a toolkit
/// </summary>
public class ComposioAuthConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("uuid")]
    public string Uuid { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("auth_scheme")]
    public string AuthScheme { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = "ENABLED";

    [JsonPropertyName("is_composio_managed")]
    public bool IsComposioManaged { get; set; }

    [JsonPropertyName("toolkit")]
    public ComposioToolkitReference? Toolkit { get; set; }

    [JsonPropertyName("credentials")]
    public Dictionary<string, object>? Credentials { get; set; }

    [JsonPropertyName("no_of_connections")]
    public int NoOfConnections { get; set; }

    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; set; }

    [JsonPropertyName("last_updated_at")]
    public string? LastUpdatedAt { get; set; }
}

/// <summary>
/// API response wrapper using 'items' array (for auth configs and tools)
/// </summary>
public class ComposioApiResponseWithItems<T>
{
    [JsonPropertyName("items")]
    public List<T> Items { get; set; } = new();

    [JsonPropertyName("next_cursor")]
    public string? NextCursor { get; set; }

    [JsonPropertyName("total_items")]
    public int? TotalItems { get; set; }

    [JsonPropertyName("total_pages")]
    public int? TotalPages { get; set; }

    [JsonPropertyName("current_page")]
    public int? CurrentPage { get; set; }
}

/// <summary>
/// API response wrapper for auth configs
/// </summary>
public class ComposioAuthConfigResponse : ComposioApiResponseWithItems<ComposioAuthConfig>
{
}

/// <summary>
/// Generic API response wrapper for Composio.dev (legacy format with 'data')
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

/// <summary>
/// Represents a connected account (OAuth or other auth scheme)
/// </summary>
public class ComposioConnectedAccount
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("user_id")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("toolkit")]
    public ComposioToolkitReference? Toolkit { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("auth_scheme")]
    public string AuthScheme { get; set; } = string.Empty;

    [JsonPropertyName("auth_config")]
    public ComposioAuthConfigInfo? AuthConfig { get; set; }

    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public string? UpdatedAt { get; set; }

    [JsonPropertyName("data")]
    public Dictionary<string, object>? Data { get; set; }

    [JsonPropertyName("is_disabled")]
    public bool IsDisabled { get; set; }

    [JsonPropertyName("status_reason")]
    public string? StatusReason { get; set; }

    [JsonPropertyName("uuid")]
    public string? Uuid { get; set; }
}

/// <summary>
/// Represents authentication configuration information
/// </summary>
public class ComposioAuthConfigInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("auth_scheme")]
    public string AuthScheme { get; set; } = string.Empty;

    [JsonPropertyName("is_composio_managed")]
    public bool IsComposioManaged { get; set; }

    [JsonPropertyName("is_disabled")]
    public bool IsDisabled { get; set; }
}

/// <summary>
/// API response wrapper for connected accounts
/// </summary>
public class ComposioConnectedAccountsResponse : ComposioApiResponseWithItems<ComposioConnectedAccount>
{
}

/// <summary>
/// Response wrapper for tool execution
/// </summary>
public class ComposioExecutionResponse
{
    [JsonPropertyName("data")]
    public Dictionary<string, object> Data { get; set; } = new();

    [JsonPropertyName("successful")]
    public bool Successful { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("log_id")]
    public string? LogId { get; set; }
}
