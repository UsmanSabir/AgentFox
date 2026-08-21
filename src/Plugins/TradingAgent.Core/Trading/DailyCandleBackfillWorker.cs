using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingAgent.Config;

namespace TradingAgent.Trading;

/// <summary>
/// Schedules the daily-candle backfill. All the work — pacing, throttle detection, progress — lives in
/// <see cref="CandleBackfillRunner"/>, which the web API and the agent's own tool drive as well; this
/// worker only decides WHEN a pass happens on its own.
///
/// Why a backfill exists at all: the exchange serves settled candles one DATE at a time (each request
/// covering every symbol), so two years of history is ~500 requests. Doing that on demand would put a
/// multi-minute stall in front of a user's question and repeat it after every restart. Done once into
/// <c>daily_bars</c>, it makes weekly levels possible and reduces steady-state cost to one request per
/// new trading day.
///
/// Passes are resumable (<c>daily_bar_coverage</c> records every date already retrieved, including
/// non-trading days), so an interrupted or throttled run continues rather than starting over — and the
/// recurring pass doubles as the mechanism that picks up each new session.
/// </summary>
public sealed class DailyCandleBackfillWorker : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan BetweenPasses = TimeSpan.FromHours(6);

    private readonly CandleBackfillRunner _runner;
    private readonly IOptions<TradingAgentOptions> _options;
    private readonly ILogger<DailyCandleBackfillWorker> _logger;

    public DailyCandleBackfillWorker(
        CandleBackfillRunner runner,
        IOptions<TradingAgentOptions> options,
        ILogger<DailyCandleBackfillWorker> logger)
    {
        _runner = runner;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.Value.Scan.BackfillYears <= 0)
        {
            _logger.LogInformation("[CandleBackfill] Disabled (Scan.BackfillYears = 0).");
            return;
        }

        // Let the app finish starting; history is never urgent.
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _runner.RunOnceAsync(null, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[CandleBackfill] Scheduled pass failed; retrying later.");
            }

            try { await Task.Delay(BetweenPasses, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
