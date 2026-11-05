using TradingAgent.Watchlist;

namespace TradingAgent.Research;

public sealed partial class PsxDataClient
{
    internal async Task<PriceAlertObservation?> GetAlertObservationAsync(string symbol, CancellationToken ct)
    {
        var points = await CachedSeriesAsync($"timeseries/int/{symbol}", IntradaySeriesTtl, ct);
        var last = points.Where(p => p.Price > 0).MaxBy(p => p.Ts);
        return last is null ? null : new(last.Price, DateTimeOffset.FromUnixTimeSeconds(last.Ts).UtcDateTime, "psx");
    }
}
