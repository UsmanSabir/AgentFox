using AgentFox.Plugins;
using Microsoft.Extensions.Logging;
using TradingAgent.Config;
using TradingAgent.Feed;
using TradingAgent.Models;
using TradingAgent.Observability;

namespace TradingAgent.Broker;

/// <summary>
/// Reads the account's cash and holdings, preferring the portal's JSON API and falling back to the
/// browser scrape in <see cref="AhkBroker.GetPortfolioAsync"/>.
///
/// <para>
/// This exists as a separate service rather than as a branch inside <see cref="AhkBroker"/> because
/// <see cref="AhkPortalClient"/> already depends on the broker (it harvests the browser's session
/// cookies), so a broker that called the portal client back would be a dependency cycle. Both
/// callers of the old method — <c>get_portfolio</c> and the protective-stop worker — take this
/// instead, and neither knows which path served the answer.
/// </para>
///
/// <para>
/// <b>The fallback is the point.</b> The API path is the fast one, but a snapshot that is merely
/// FAST is worth nothing next to one that is correct: the numbers here size real orders. So a failed
/// API read is never surfaced as an empty portfolio — it re-runs the browser scrape, which is the
/// path with years of live exercise behind it. The only thing that changes when the API works is how
/// long it took and whether the browser gate was taken.
/// </para>
/// </summary>
public sealed class PortfolioReader
{
    private readonly AhkPortalClient _portal;
    private readonly AhkBroker _broker;
    private readonly IRuntimePluginOptions<AhkConfig> _config;
    private readonly ILogger<PortfolioReader> _logger;
    private readonly TradingActivityLog? _activity;

    public PortfolioReader(
        AhkPortalClient portal,
        AhkBroker broker,
        IRuntimePluginOptions<AhkConfig> config,
        ILogger<PortfolioReader> logger,
        TradingActivityLog? activity = null)
    {
        _portal = portal;
        _broker = broker;
        _config = config;
        _logger = logger;
        _activity = activity;
    }

    /// <summary>Which path produced the most recent snapshot, for the status surfaces.</summary>
    public string LastSource { get; private set; } = "(not read yet)";

    public async Task<PortfolioSnapshot> GetPortfolioAsync(CancellationToken ct = default)
    {
        if (_config.Current.PreferDirectApiForPortfolio)
        {
            var viaApi = await TryReadViaApiAsync(ct);
            if (viaApi is not null)
            {
                LastSource = "portal JSON API";
                return viaApi;
            }

            // Not an error the caller needs to handle — the browser path below still answers. It is
            // logged at warning because a permanent silent fallback would quietly restore the old
            // latency and the old feed contention with nothing on screen to say so.
            _logger.LogWarning(
                "[Portfolio] The direct API read did not succeed; falling back to the browser scrape.");
            _activity?.Warn("Broker", "Portfolio: direct API read failed, using the browser scrape");
        }

        var snapshot = await _broker.GetPortfolioAsync();
        LastSource = "browser scrape";
        return snapshot;
    }

    /// <summary>
    /// Builds a snapshot from <c>GetCollaterals</c> + <c>GetAccountBalance</c>, or null when the
    /// holdings could not be read.
    ///
    /// <para>
    /// Holdings failing is what triggers the fallback; the balance failing is not. A null balance is
    /// a value <see cref="PortfolioSnapshot"/> already models honestly — the consumers are required
    /// to render it as unknown — whereas holdings that could not be read would be indistinguishable
    /// from an account that owns nothing, and that difference decides whether a sell is even possible.
    /// </para>
    /// </summary>
    private async Task<PortfolioSnapshot?> TryReadViaApiAsync(CancellationToken ct)
    {
        try
        {
            var holdingsTask = _portal.GetCollateralsAsync(ct);
            var balanceTask = _portal.GetAccountBalanceAsync(ct);
            await Task.WhenAll(holdingsTask, balanceTask);

            var rows = await holdingsTask;
            if (rows is null) return null;

            var balance = await balanceTask;
            var warnings = new List<string>();
            if (balance is null)
            {
                warnings.Add(
                    "The available cash balance could not be read from the broker, so it is unknown. " +
                    "Do not treat it as zero.");
            }

            var holdings = rows
                .Where(r => !string.IsNullOrWhiteSpace(r.Symbol))
                .Select(ToHolding)
                .ToList();

            return new PortfolioSnapshot
            {
                AvailableBalancePkr = balance,
                BalanceSource = balance is null ? null : "GET /Home/GetAccountBalance",
                HoldingsAvailable = true,
                Holdings = holdings,
                TotalInvestment = SumOrNull(holdings.Select(h => h.InvestmentValue)),
                TotalCurrentValue = SumOrNull(holdings.Select(h => h.CurrentValue)),
                Warnings = warnings
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Portfolio] The direct API portfolio read threw.");
            return null;
        }
    }

    /// <summary>
    /// Maps one collateral row onto the shared holding shape.
    ///
    /// <para>
    /// <c>amount</c> and <c>unsettled</c> are taken AS REPORTED rather than recomputed, having been
    /// verified against every row of a live capture as exactly price × quantity and
    /// (mtm − avg) × quantity. <c>InvestmentValue</c> has no reported equivalent, so it is derived —
    /// and only when both inputs are present, because a cost basis of zero would read as a free
    /// position and make every P/L percentage downstream nonsense.
    /// </para>
    ///
    /// <para>
    /// Money is rounded to paisa. The portal computes these in binary floating point and serialises
    /// the result verbatim, so a real capture contains <c>26215.000000000004</c>; carried into a
    /// decimal that noise survives every later sum, and the account total renders as
    /// <c>324972.00000000001</c>. Rounding here is not a loss of precision — PKR has two decimal
    /// places and the extra digits are an artifact of the portal's arithmetic, not information.
    /// Per-share prices are left exactly as sent, since those are the portal's own quoted values.
    /// </para>
    /// </summary>
    private static HoldingPosition ToHolding(AhkCollateralHolding r)
    {
        var quantity = r.QuantityTotal;
        var avg = r.AvgRateBuy;
        var invested = quantity is not null && avg is not null ? Money(quantity * avg) : null;

        return new HoldingPosition
        {
            Symbol = (r.Symbol ?? "").Trim().ToUpperInvariant(),
            Quantity = quantity,
            AverageBuyPrice = avg,
            InvestmentValue = invested,
            CurrentPrice = r.MtmPrice,
            CurrentValue = Money(r.Amount),
            ProfitLoss = Money(r.Unsettled),
            ProfitLossPercent = invested is > 0 && r.Unsettled is not null
                ? Math.Round(r.Unsettled.Value / invested.Value * 100m, 2)
                : null
        };
    }

    /// <summary>Rounds a PKR amount to paisa, preserving null as unknown.</summary>
    private static decimal? Money(decimal? value) =>
        value is null ? null : Math.Round(value.Value, 2, MidpointRounding.AwayFromZero);

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
