using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentFox.Plugins.Interfaces;
using TradingAgent.Config;
using TradingAgent.Persistence;

namespace TradingAgent.Tools;

/// <summary>Persists a non-executable proposal for later deterministic validation and approval.</summary>
public sealed class CreateTradeProposalTool : BaseTool
{
    private readonly ITradingRepository _repository;
    private readonly TradingPolicyProvider _policyProvider;

    public CreateTradeProposalTool(
        ITradingRepository repository,
        TradingPolicyProvider policyProvider)
    {
        _repository = repository;
        _policyProvider = policyProvider;
    }

    public override string Name => "create_trade_proposal";
    public override string Description =>
        "Persist a non-executable PSX trade proposal. This does not submit an order.";

    public override Dictionary<string, ToolParameter> Parameters => new()
    {
        ["orders"] = new()
        {
            Type = "array",
            Required = true,
            Description = "Proposed order legs with action, symbol, optional quantity, price, target, stop_loss, and confidence.",
            JsonSchema = """
                {"type":"array","minItems":1,"items":{"type":"object","properties":{
                  "action":{"type":"string","enum":["BUY","SELL"]},
                  "symbol":{"type":"string"},"quantity":{"type":"integer","minimum":1},
                  "price":{"type":"number","exclusiveMinimum":0},
                  "target":{"type":"number","exclusiveMinimum":0},
                  "stop_loss":{"type":"number","exclusiveMinimum":0},
                  "confidence":{"type":"string"}},"required":["action","symbol"]}}
                """
        },
        ["source_message"] = new()
        {
            Type = "string",
            Required = false,
            Description = "Original source message used for durable proposal de-duplication."
        },
        ["rationale"] = new()
        {
            Type = "string",
            Required = false,
            Description = "Short explanation of the proposal; never include secrets."
        }
    };

    protected override async Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var json = JsonSerializer.Serialize(new
        {
            orders = arguments.GetValueOrDefault("orders"),
            source_message = arguments.GetValueOrDefault("source_message")?.ToString(),
            rationale = arguments.GetValueOrDefault("rationale")?.ToString()
        });
        using var doc = JsonDocument.Parse(json);
        var orders = doc.RootElement.GetProperty("orders");
        if (orders.ValueKind != JsonValueKind.Array || orders.GetArrayLength() == 0)
            return ToolResult.Fail("orders must contain at least one proposal leg.");

        foreach (var order in orders.EnumerateArray())
        {
            if (!order.TryGetProperty("action", out var action)
                || action.GetString() is not ("BUY" or "SELL")
                || !order.TryGetProperty("symbol", out var symbol)
                || string.IsNullOrWhiteSpace(symbol.GetString()))
                return ToolResult.Fail("Each proposal leg requires BUY/SELL action and a symbol.");
        }

        var policy = _policyProvider.Current();
        var source = arguments.GetValueOrDefault("source_message")?.ToString() ?? "";
        var keyMaterial = $"{source.Trim().ToLowerInvariant()}|{json}|{policy.Version}";
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial)));
        var id = await _repository.CreateProposalAsync(key, json, policy.Version);
        return ToolResult.Ok(JsonSerializer.Serialize(new
        {
            proposal_id = id,
            status = "proposed",
            executable = false,
            policy_version = policy.Version
        }));
    }
}
