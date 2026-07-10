using TradingAgent.Models;

namespace TradingAgent.Broker;

public interface IBrokerAdapter
{
    Task<IReadOnlyDictionary<string, decimal?>> GetMarketPricesAsync(IReadOnlyList<string> symbols);

    Task<IReadOnlyList<IReadOnlyList<OrderResult>>> PlaceOrderGroupsAsync(
        IReadOnlyList<IReadOnlyList<TradingSignal>> groups);
}

public sealed class AhkBrowserBrokerAdapter : IBrokerAdapter
{
    private readonly AhkBroker _broker;

    public AhkBrowserBrokerAdapter(AhkBroker broker) => _broker = broker;

    public Task<IReadOnlyDictionary<string, decimal?>> GetMarketPricesAsync(IReadOnlyList<string> symbols) =>
        _broker.GetMarketPricesAsync(symbols);

    public Task<IReadOnlyList<IReadOnlyList<OrderResult>>> PlaceOrderGroupsAsync(
        IReadOnlyList<IReadOnlyList<TradingSignal>> groups) =>
        _broker.PlaceOrderGroupsAsync(groups);
}
