using System.Text.Json;
using AgentFox.Plugins.Interfaces;
using Microsoft.Extensions.Logging;
using TradingAgent.Broker;

namespace TradingAgent.Tools;

/// <summary>
/// Read-only account snapshot for the agent: available cash plus every current holding with
/// share count, cost basis, and live market value. Backed by real broker data via
/// <see cref="IBrokerAccountReader"/> — the agent is told to NEVER invent balances,
/// and this tool is how it gets the real numbers. Any extraction gap is surfaced in
/// <c>warnings</c> so the agent reports "unknown" rather than a guess.
///
/// <para>
/// Broker-neutral by construction: previously this took the concrete <c>PortfolioReader</c>, which
/// falls back to a browser scrape and requires the AHK browser-cookie session regardless of which
/// broker adapter a premium account actually uses. The output JSON shape here is unchanged from
/// before — only the source moved.
/// </para>
/// </summary>
public sealed class GetPortfolioTool : BaseTool
{
    private readonly IBrokerAccountReader _reader;
    private readonly ILogger<GetPortfolioTool> _logger;

    private static readonly JsonSerializerOptions _snakeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public GetPortfolioTool(IBrokerAccountReader reader, ILogger<GetPortfolioTool> logger)
    {
        _reader = reader;
        _logger = logger;
    }

    public override string Name => "get_portfolio";

    public override string Description =>
        "Read the REAL account state from the broker portal: available cash balance (PKR) and all " +
        "currently held stocks with number of shares, average buy price, invested amount, current " +
        "price, current value, and profit/loss. Use this before sizing or recommending any trade, " +
        "and whenever the user asks about their balance, holdings, or portfolio. Values missing " +
        "from the portal are null — report them as unknown, never estimate them.";

    public override Dictionary<string, ToolParameter> Parameters => new();

    protected override async Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        try
        {
            var snapshot = await _reader.ReadAccountAsync();

            var balance = snapshot.Balances.FirstOrDefault(b => b.Key == "available_cash");

            return ToolResult.Ok(JsonSerializer.Serialize(new
            {
                available_balance_pkr = balance?.Value,
                balance_source        = balance?.Attributes?.GetValueOrDefault("source"),
                holdings = snapshot.Holdings.Select(h => new
                {
                    symbol              = h.Symbol,
                    shares              = h.Quantity,
                    average_buy_price   = h.AverageCost,
                    invested_pkr        = h.CostValue,
                    current_price       = h.MarketPrice,
                    current_value_pkr   = h.MarketValue,
                    profit_loss_pkr     = h.UnrealizedProfitLoss,
                    profit_loss_percent = h.UnrealizedProfitLossPercent
                }),
                holdings_count       = snapshot.Holdings.Count,
                total_invested_pkr   = SumOrNull(snapshot.Holdings.Select(h => h.CostValue)),
                total_value_pkr      = SumOrNull(snapshot.Holdings.Select(h => h.MarketValue)),
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

    /// <summary>
    /// Sums only when every part is known. A total built by skipping the unknown rows would look
    /// like a complete figure while understating the account.
    /// </summary>
    private static decimal? SumOrNull(IEnumerable<decimal?> values)
    {
        decimal total = 0m;
        var any = false;

        foreach (var v in values)
        {
            if (v is null) return null;
            total += v.Value;
            any = true;
        }

        return any ? total : null;
    }
}
