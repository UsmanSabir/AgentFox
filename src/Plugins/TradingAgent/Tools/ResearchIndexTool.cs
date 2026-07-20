using System.Text.Json;
using System.Text.Json.Serialization;
using AgentFox.Plugins.Interfaces;
using AgentFox.Plugins.Research;
using Microsoft.Extensions.Logging;
using TradingAgent.Research;

namespace TradingAgent.Tools;

/// <summary>Fetches current and historical evidence for a PSX index such as KSE30.</summary>
public sealed class ResearchIndexTool : BaseTool
{
    private readonly PsxDataClient _dataClient;
    private readonly ILogger<ResearchIndexTool> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ResearchIndexTool(PsxDataClient dataClient, ILogger<ResearchIndexTool> logger)
    {
        _dataClient = dataClient;
        _logger = logger;
    }

    public override string Name => "research_index";

    public override string Description =>
        "Fetch read-only current, trend, range, and volume evidence for a Pakistan Stock Exchange " +
        "index (for example KSE30 or KSE100). Returns official PSX source URLs and retrieval time; " +
        "never places or recommends an order.";

    public override Dictionary<string, ToolParameter> Parameters => new()
    {
        ["index"] = new()
        {
            Type = "string",
            Description = "Official PSX index symbol, for example KSE30, KSE100, KMI30, or ALLSHR.",
            Required = true
        }
    };

    protected override async Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var index = arguments.GetValueOrDefault("index")?.ToString()?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(index))
            return ToolResult.Fail("Parameter 'index' is required.");

        try
        {
            _logger.LogInformation("[ResearchIndex] Researching {Index}…", index);
            var data = await _dataClient.GatherIndexAsync(index);

            var scope = ResearchReferenceScope.Current;
            if (scope is not null)
            {
                foreach (var url in data.SourceUrls)
                    scope.Add(url, $"PSX index data: {index}", "PSX Data Portal");
            }

            return ToolResult.Ok(JsonSerializer.Serialize(new
            {
                index = data.Index,
                quote = data.Quote,
                retrieved_at_utc = data.RetrievedAtUtc,
                source_urls = data.SourceUrls
            }, JsonOptions));
        }
        catch (ArgumentException ex)
        {
            return ToolResult.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ResearchIndex] Index fetch failed for {Index}.", index);
            return ToolResult.Fail($"PSX index research failed: {ex.Message}");
        }
    }
}
