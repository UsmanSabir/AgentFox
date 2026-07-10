using System.Text.Json;
using AgentFox.Plugins.Interfaces;
using TradingAgent.Config;
using TradingAgent.Market;
using TradingAgent.Persistence;

namespace TradingAgent.Tools;

public sealed class GetTradingStatusTool : BaseTool
{
    private readonly ITradingRepository _repository;
    private readonly TradingPolicyProvider _policyProvider;
    private readonly IMarketCalendar _calendar;

    public GetTradingStatusTool(
        ITradingRepository repository,
        TradingPolicyProvider policyProvider,
        IMarketCalendar calendar)
    {
        _repository = repository;
        _policyProvider = policyProvider;
        _calendar = calendar;
    }

    public override string Name => "get_trading_status";
    public override string Description =>
        "Read the current trading policy, market state, and operational ledger health. Does not trade.";
    public override Dictionary<string, ToolParameter> Parameters => new();

    protected override async Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var status = await _repository.GetStatusAsync();
        var policy = _policyProvider.Current();
        var market = _calendar.GetStatus();
        return ToolResult.Ok(JsonSerializer.Serialize(new
        {
            execution_mode = policy.ExecutionMode,
            auto_execute = policy.AutoExecute,
            min_confidence = policy.MinConfidence,
            policy_version = policy.Version,
            market_open = market.IsOpen,
            market_reason = market.Reason,
            ledger = status
        }));
    }
}
