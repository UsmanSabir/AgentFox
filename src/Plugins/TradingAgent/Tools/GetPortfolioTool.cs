using System.Text.Json;
using AgentFox.Plugins.Interfaces;
using Microsoft.Extensions.Logging;
using TradingAgent.Broker;

namespace TradingAgent.Tools;

/// <summary>
/// Read-only account snapshot for the agent: available cash plus every current holding with
/// share count, cost basis, and live market value. Backed by a real portal scrape
/// (<see cref="AhkBroker.GetPortfolioAsync"/>) — the agent is told to NEVER invent balances,
/// and this tool is how it gets the real numbers. Any extraction gap is surfaced in
/// <c>warnings</c> so the agent reports "unknown" rather than a guess.
/// </summary>
public sealed class GetPortfolioTool : BaseTool
{
    private readonly AhkBroker _broker;
    private readonly ILogger<GetPortfolioTool> _logger;

    private static readonly JsonSerializerOptions _snakeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public GetPortfolioTool(AhkBroker broker, ILogger<GetPortfolioTool> logger)
    {
        _broker = broker;
        _logger = logger;
    }

    public override string Name => "get_portfolio";

    public override string Description =>
        "Read the REAL account state from the broker portal: available cash balance (PKR) and all " +
        "currently held stocks with number of shares, average buy price, invested amount, current " +
        "price, current value, and profit/loss. Use this before sizing or recommending any trade, " +
        "and whenever the user asks about their balance, holdings, or portfolio. Opens a browser " +
        "session, so it takes several seconds. Values missing from the portal are null — report " +
        "them as unknown, never estimate them.";

    public override Dictionary<string, ToolParameter> Parameters => new();

    protected override async Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        try
        {
            var snapshot = await _broker.GetPortfolioAsync();

            return ToolResult.Ok(JsonSerializer.Serialize(new
            {
                available_balance_pkr = snapshot.AvailableBalancePkr,
                balance_source        = snapshot.BalanceSource,
                holdings = snapshot.Holdings.Select(h => new
                {
                    symbol              = h.Symbol,
                    shares              = h.Quantity,
                    average_buy_price   = h.AverageBuyPrice,
                    invested_pkr        = h.InvestmentValue,
                    current_price       = h.CurrentPrice,
                    current_value_pkr   = h.CurrentValue,
                    profit_loss_pkr     = h.ProfitLoss,
                    profit_loss_percent = h.ProfitLossPercent
                }),
                holdings_count       = snapshot.Holdings.Count,
                total_invested_pkr   = snapshot.TotalInvestment,
                total_value_pkr      = snapshot.TotalCurrentValue,
                retrieved_at_utc     = snapshot.RetrievedAtUtc,
                warnings             = snapshot.Warnings
            }, _snakeOptions));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[GetPortfolio] Portfolio read failed.");
            return ToolResult.Fail(
                $"Could not read the portfolio from the broker portal: {ex.Message}. " +
                "Do not guess balances or holdings — tell the user the live read failed.");
        }
    }
}
