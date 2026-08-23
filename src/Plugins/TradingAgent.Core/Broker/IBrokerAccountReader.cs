using TradingAgent.Feed;
using TradingAgent.Models;

namespace TradingAgent.Broker;

/// <summary>
/// Current broker account reader. Dashboard code depends only on this contract; each broker adapter
/// owns the translation from its native balances, positions and order-book fields.
/// </summary>
public interface IBrokerAccountReader
{
    Task<BrokerAccountSnapshot> ReadAccountAsync(CancellationToken ct = default);
}

/// <summary>AHK implementation of the broker-neutral account contract.</summary>
public sealed class AhkBrokerAccountReader : IBrokerAccountReader
{
    private readonly PortfolioReader _portfolio;
    private readonly AhkPortalClient _portal;

    public AhkBrokerAccountReader(PortfolioReader portfolio, AhkPortalClient portal)
    {
        _portfolio = portfolio;
        _portal = portal;
    }

    public async Task<BrokerAccountSnapshot> ReadAccountAsync(CancellationToken ct = default)
    {
        PortfolioSnapshot? portfolio = null;
        OrderBookRead? orderBook = null;
        var warnings = new List<string>();

        // The two reads are independent. Return whichever sections succeeded so a temporary order-book
        // failure does not hide valid holdings, while availability flags prevent failure from looking
        // like an honestly empty account.
        try
        {
            portfolio = await _portfolio.GetPortfolioAsync(ct);
            warnings.AddRange(portfolio.Warnings);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            warnings.Add($"Balances and holdings could not be read: {ex.Message}");
        }

        try
        {
            orderBook = await _portal.GetOutstandingAsync(ct: ct);
            if (orderBook.Value.Error is { Length: > 0 } error) warnings.Add(error);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            warnings.Add($"The working order book could not be read: {ex.Message}");
        }

        return new BrokerAccountSnapshot
        {
            BrokerId = "ahk",
            BrokerName = "AHK Securities",
            AccountLabel = _portal.AccountCode,
            BalancesAvailable = portfolio is not null && portfolio.AvailableBalancePkr is not null,
            HoldingsAvailable = portfolio?.HoldingsAvailable == true,
            OrdersAvailable = orderBook is { Ok: true },
            Balances = portfolio is null
                ? []
                :
                [
                    new BrokerAccountBalance
                    {
                        Key = "available_cash",
                        Label = "Available cash",
                        Value = portfolio.AvailableBalancePkr,
                        Currency = "PKR",
                        Attributes = new Dictionary<string, string?>
                        {
                            ["source"] = portfolio.BalanceSource
                        }
                    }
                ],
            Holdings = portfolio?.Holdings.Select(MapHolding).ToList() ?? [],
            Orders = orderBook is { Ok: true } book
                ? book.Orders.Select(MapOrder).ToList()
                : [],
            RetrievedAtUtc = portfolio?.RetrievedAtUtc ?? DateTime.UtcNow,
            Warnings = warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Attributes = new Dictionary<string, string?>
            {
                ["portfolioSource"] = portfolio is null ? null : _portfolio.LastSource
            }
        };
    }

    internal static BrokerAccountHolding MapHolding(HoldingPosition h) => new()
    {
        InstrumentId = h.Symbol,
        Symbol = h.Symbol,
        Exchange = "PSX",
        AssetType = "equity",
        Quantity = h.Quantity,
        AverageCost = h.AverageBuyPrice,
        MarketPrice = h.CurrentPrice,
        CostValue = h.InvestmentValue,
        MarketValue = h.CurrentValue,
        UnrealizedProfitLoss = h.ProfitLoss,
        UnrealizedProfitLossPercent = h.ProfitLossPercent,
        Currency = "PKR"
    };

    internal static BrokerAccountOrder MapOrder(AhkOutstandingOrder o)
    {
        var side = o.Type?.Trim().ToUpperInvariant();
        return new BrokerAccountOrder
        {
            OrderId = o.OrderNo?.Trim() ?? "",
            ExternalOrderId = o.HOrderNo?.Trim(),
            InstrumentId = o.Scrip?.Trim().ToUpperInvariant() ?? "",
            Symbol = o.Scrip?.Trim().ToUpperInvariant(),
            Exchange = string.IsNullOrWhiteSpace(o.Market) ? "PSX" : o.Market.Trim(),
            Side = side == "SEL" ? "SELL" : side,
            Status = o.Action?.Trim(),
            RemainingQuantity = o.Remaining,
            Price = o.Price,
            Currency = "PKR",
            PlacedAt = o.Time,
            Attributes = new Dictionary<string, string?>
            {
                ["flag"] = o.Flag,
                ["trader"] = o.Trader
            }
        };
    }
}
