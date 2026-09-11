using Microsoft.Extensions.Logging;
using TradingAgent.AhlAnalytics;
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
    private readonly AhlAnalyticsClient _analytics;
    private readonly IAnalyticsSsoUrlProvider _analyticsSso;
    private readonly ILogger<AhkBrokerAccountReader> _logger;

    public AhkBrokerAccountReader(
        PortfolioReader portfolio,
        AhkPortalClient portal,
        AhlAnalyticsClient analytics,
        IAnalyticsSsoUrlProvider analyticsSso,
        ILogger<AhkBrokerAccountReader> logger)
    {
        _portfolio = portfolio;
        _portal = portal;
        _analytics = analytics;
        _analyticsSso = analyticsSso;
        _logger = logger;
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

        AhlSnapshotData? market = null;
        if (_analytics.Enabled && portfolio?.Holdings is { Count: > 0 })
        {
            try
            {
                // Portfolio refresh is user-initiated, but still must not silently launch the browser
                // login required by the community AHK provider. Premium's SOAP provider reports this
                // as safe, while a warm community broker session can reuse its existing login.
                market = await _analytics.GetMarketSnapshotAsync(
                    allowHandshake: _analyticsSso.CanHandshakeSafely, ct: ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // Classification is additive context. A failed analytics read must never turn an
                // otherwise reliable broker account snapshot into a failed account read.
                _logger.LogDebug(ex, "[TradingAgent] Sector classification was unavailable for the account view.");
            }
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
            Holdings = portfolio?.Holdings.Select(holding => MapHolding(holding, market)).ToList() ?? [],
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

    internal static BrokerAccountHolding MapHolding(HoldingPosition h, AhlSnapshotData? market = null)
    {
        var symbol = h.Symbol.Trim().ToUpperInvariant();
        AhlEquity? equity = null;
        market?.Equities?.TryGetValue(symbol, out equity);
        return new BrokerAccountHolding
        {
            InstrumentId = symbol,
            Symbol = symbol,
            Exchange = "PSX",
            AssetType = "equity",
            SectorCode = equity?.SectorCode,
            Sector = AhlSectors.Name(equity?.SectorCode) ?? equity?.SectorCode,
            Quantity = h.Quantity,
            AverageCost = h.AverageBuyPrice,
            MarketPrice = h.CurrentPrice,
            CostValue = h.InvestmentValue,
            MarketValue = h.CurrentValue,
            UnrealizedProfitLoss = h.ProfitLoss,
            UnrealizedProfitLossPercent = h.ProfitLossPercent,
            Currency = "PKR"
        };
    }

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
