using System.Globalization;
using AgentFox.Plugins.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingAgent.Market;
using TradingAgent.Persistence;
using TradingAgent.Research;

namespace TradingAgent.Watchlist;

public sealed class PriceAlertWorker(
    SqliteTradingRepository repository,
    CompositeLiveQuoteSource quotes,
    PsxDataClient psx,
    IMarketCalendar calendar,
    ILogger<PriceAlertWorker> logger,
    IUserNotifier? notifier = null,
    IPriceAlertPush? push = null,
    TimeProvider? clock = null,
    IIndexAlertSource? indexSource = null) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan QuoteAge = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan IndexReadDeadline = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan DeliveryWindow = TimeSpan.FromMinutes(30);
    private int _verificationOffset;
    // Whether the last pass that needed the KSE 100 found no fresh reading anywhere. One bool, so an outage logs one
    // Warning when it starts and one when it ends, and the alert list can say why an index alert is not watching.
    private int _indexUnavailable;

    /// <summary>True while the market is open, an index alert is watching, and no source has a fresh KSE 100 reading.</summary>
    public bool IndexUnavailable => Volatile.Read(ref _indexUnavailable) == 1;
    private DateTime Now() => (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;
    private IReadOnlyDictionary<string, PriceAlertObservation> _latest = new Dictionary<string, PriceAlertObservation>();

    public PriceAlertObservation? Latest(PriceAlert alert)
    {
        var observation = Volatile.Read(ref _latest).GetValueOrDefault(alert.Symbol);
        return observation is not null && IsFresh(observation, Now()) ? observation : null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        var nextSweep = DateTime.MinValue;
        do
        {
            try
            {
                var now = Now();
                if (now >= nextSweep)
                {
                    await repository.PrunePriceAlertsAsync(now, stoppingToken);
                    nextSweep = now.AddHours(1);
                }
                await TickAsync(now, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Price alert pass failed.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task TickAsync(DateTime now, CancellationToken ct)
    {
        var alerts = await repository.ReadPriceAlertsAsync(ct);
        if (alerts.Count == 0)
        {
            Volatile.Write(ref _latest, new Dictionary<string, PriceAlertObservation>());
            Volatile.Write(ref _indexUnavailable, 0);
            return;
        }
        var market = calendar.GetStatus(now);
        var latest = new Dictionary<string, PriceAlertObservation>();
        var watching = alerts.Where(a => a.State == "watching").ToArray();
        var active = watching.Where(a => a.ExpiresUtc > now).ToArray();
        // Nothing is asked while the market is shut or no index alert is watching, so there is nothing to report.
        if (!market.IsOpen || active.All(a => a.Symbol != PriceAlert.Index)) Volatile.Write(ref _indexUnavailable, 0);
        var observations = market.IsOpen ? await ObserveAsync(active, ct) : new Dictionary<string, PriceAlertObservation>();
        now = Now();
        market = calendar.GetStatus(now);
        var confirmed = market.IsOpen ? await ConfirmPsxTargetsAsync(active, observations, now, ct) : [];
        now = Now();
        market = calendar.GetStatus(now);
        foreach (var alert in watching)
        {
            if (alert.ExpiresUtc <= now)
            {
                await repository.WritePriceAlertAsync(alert with { State = "expired", FinishedUtc = now }, alert, ct);
                continue;
            }
            if (!market.IsOpen || !observations.TryGetValue(alert.Symbol, out var observation)
                || !IsFresh(observation, now))
                continue;

            latest[alert.Symbol] = observation;
            if (!alert.Matches(observation.Value)) continue;
            if (observation.Source == "psx" && alert.Symbol != PriceAlert.Index)
            {
                if (!confirmed.TryGetValue(alert.Symbol, out var trade) || !IsFresh(trade, now) || !alert.Matches(trade.Value))
                    continue;
                observation = trade;
            }
            var triggered = alert with
            {
                State = "pending", Trigger = observation, FinishedUtc = now, NextDeliveryUtc = now
            };
            await repository.WritePriceAlertAsync(triggered, alert, ct);
        }
        Volatile.Write(ref _latest, latest);

        var delivery = await repository.ReadPriceAlertsAsync(ct);
        foreach (var alert in delivery.Where(a => a.Trigger is not null && a.FinishedUtc <= now - DeliveryWindow
                     && (a.State == "pending" || a.State == "delivered" && !a.PushQueued)))
        {
            await repository.WritePriceAlertAsync(alert with
            {
                State = alert.State == "pending" ? "delivery-failed" : alert.State,
                NextDeliveryUtc = null
            }, alert, ct);
        }

        foreach (var alert in delivery.Where(a => a.Trigger is not null && a.NextDeliveryUtc <= now
                     && a.FinishedUtc > now - DeliveryWindow).OrderBy(a => a.NextDeliveryUtc).Take(8))
            await DeliverAsync(alert, now, ct);
    }

    private async Task<Dictionary<string, PriceAlertObservation>> ObserveAsync(PriceAlert[] alerts, CancellationToken ct)
    {
        var observations = new Dictionary<string, PriceAlertObservation>(StringComparer.OrdinalIgnoreCase);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var index = alerts.Any(a => a.Symbol == PriceAlert.Index) ? ObserveIndexAsync(ct) : null;
        if (alerts.Any(a => a.Symbol != PriceAlert.Index))
        {
            LiveQuoteSnapshot snapshot;
            try { snapshot = await quotes.GetQuotesAsync(deadline.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { snapshot = LiveQuoteSnapshot.Empty("composite"); }
            foreach (var symbol in alerts.Select(a => a.Symbol).Distinct())
                if (symbol != PriceAlert.Index && snapshot.Quotes.TryGetValue(symbol, out var quote)
                    && quote.Current is > 0m && quote.Volume is > 0 && quote.BoardState != "CLS")
                    observations[symbol] = new(quote.Current.Value, ObservedAt(quote), quote.Source);
        }
        if (index is not null && await index is { } indexQuote) observations[PriceAlert.Index] = indexQuote;
        return observations;
    }

    // The edition's own index reading first (the broker's feed), PSX's intraday series when it has nothing fresh. Each
    // gets its own deadline, so a broker feed that accepts and never answers still leaves PSX time to be asked.
    private async Task<PriceAlertObservation?> ObserveIndexAsync(CancellationToken ct)
    {
        string? brokerProblem = null;
        if (indexSource is not null)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(IndexReadDeadline);
            try
            {
                var reading = await indexSource.ObserveIndexAsync(deadline.Token);
                if (reading is not null && IsFresh(reading, Now()))
                {
                    ReportIndex(available: true);
                    return reading;
                }
                brokerProblem = reading is null ? "no readings for today" : $"its last reading is from {PsxTime.Now(reading.ObservedUtc):HH:mm:ss} PKT";
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                brokerProblem = ex.Message;
            }
        }

        string psxProblem;
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            deadline.CancelAfter(IndexReadDeadline);
            try
            {
                var reading = await psx.GetAlertObservationAsync(PriceAlert.Index, deadline.Token);
                if (reading is not null && IsFresh(reading, Now()))
                {
                    ReportIndex(available: true);
                    return reading;
                }
                psxProblem = reading is null ? "no readings for today" : $"its last reading is from {PsxTime.Now(reading.ObservedUtc):HH:mm:ss} PKT";
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                psxProblem = ex.Message;
            }
        }

        ReportIndex(available: false, brokerProblem is null
            ? $"PSX: {psxProblem}"
            : $"broker feed: {brokerProblem}; PSX: {psxProblem}");
        return null;
    }

    private void ReportIndex(bool available, string? problem = null)
    {
        if (available)
        {
            if (Interlocked.Exchange(ref _indexUnavailable, 0) == 1)
                logger.LogWarning("KSE 100 price alerts have a current reading again.");
            return;
        }
        if (Interlocked.Exchange(ref _indexUnavailable, 1) == 0)
            logger.LogWarning("KSE 100 price alerts cannot fire: no current KSE 100 reading ({Problem}).", problem);
        else
            logger.LogDebug("KSE 100 alert data still unavailable ({Problem}).", problem);
    }

    private static DateTime ObservedAt(PsxLiveQuote quote)
    {
        if (!TimeOnly.TryParseExact(quote.LastTradeTime, "HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var time))
            return quote.RetrievedAtUtc;

        var date = PsxTime.Today(quote.RetrievedAtUtc);
        return TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(time), PsxTime.Zone);
    }

    // Market Watch supplies a receipt time. Confirm apparent hits against the timestamped
    // trade series so a prior-session row at the open cannot fire.
    private async Task<Dictionary<string, PriceAlertObservation>> ConfirmPsxTargetsAsync(
        PriceAlert[] active, Dictionary<string, PriceAlertObservation> observations, DateTime now, CancellationToken ct)
    {
        var candidates = active.Where(a => a.Symbol != PriceAlert.Index
                && observations.TryGetValue(a.Symbol, out var quote) && quote.Source == "psx"
                && IsFresh(quote, now) && a.Matches(quote.Value))
            .Select(a => a.Symbol).Distinct().Order(StringComparer.Ordinal).ToArray();
        if (candidates.Length == 0) return [];
        var start = _verificationOffset % candidates.Length;
        var count = Math.Min(4, candidates.Length);
        _verificationOffset = (start + count) % candidates.Length;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));

        var readings = await Task.WhenAll(Enumerable.Range(0, count).Select(async i =>
        {
            var symbol = candidates[(start + i) % candidates.Length];
            try { return (Symbol: symbol, Quote: await psx.GetAlertObservationAsync(symbol, deadline.Token)); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogDebug(ex, "PSX alert data unavailable for {Symbol}.", symbol);
                return (Symbol: symbol, Quote: (PriceAlertObservation?)null);
            }
        }));
        return readings.Where(x => x.Quote is not null).ToDictionary(x => x.Symbol, x => x.Quote!);
    }

    internal static bool IsFresh(PriceAlertObservation observation, DateTime now) =>
        observation.Value > 0m && observation.ObservedUtc >= now - QuoteAge
        && observation.ObservedUtc <= now && PsxTime.Today(observation.ObservedUtc) == PsxTime.Today(now);

    private async Task DeliverAsync(PriceAlert alert, DateTime now, CancellationToken ct)
    {
        var claim = alert with { NextDeliveryUtc = now.AddMinutes(1) };
        if (!await repository.WritePriceAlertAsync(claim, alert, ct)) return;

        var queued = claim.PushQueued;
        if (!queued && push is not null)
        {
            try { queued = await push.TryQueueAsync(claim, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Could not queue mobile price alert {AlertId}.", claim.Id);
            }
        }

        // A channel is retried only after a clear zero. A timeout may still be sending, so it is
        // recorded as unconfirmed and never retried: retrying every minute repeated the same alert
        // up to 30 times on a channel that is slow rather than broken.
        var sent = claim.RecipientCount;
        var unconfirmed = claim.ChannelUnconfirmed;
        if (sent == 0 && !unconfirmed && notifier is not null)
        {
            try
            {
                var topic = claim.Symbol == PriceAlert.Index ? TradingTopics.IndexAlert : TradingTopics.PriceAlert;
                sent = await notifier.NotifyAsync(Message(claim), topic, ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
            }
            catch (TimeoutException ex)
            {
                unconfirmed = true;
                logger.LogWarning(ex, "Price alert {AlertId} channel delivery is unconfirmed; it will not be resent.", claim.Id);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Could not deliver price alert {AlertId}.", claim.Id);
            }
        }

        // A queued app push is a delivery. With one queued, a zero-recipient channel is not
        // retried: zero is also what a deployment with no channel subscribed reports, and
        // retrying it marked every phone-only alert "delivery-failed" after 30 minutes.
        var delivered = sent > 0 || unconfirmed || queued;
        await repository.WritePriceAlertAsync(claim with
        {
            State = delivered ? "delivered" : "pending",
            RecipientCount = sent,
            ChannelUnconfirmed = unconfirmed,
            PushQueued = queued,
            NextDeliveryUtc = delivered && (push is null || queued) ? null : claim.NextDeliveryUtc
        }, claim, ct);
    }

    private static string Message(PriceAlert alert)
    {
        var index = alert.Symbol == PriceAlert.Index;
        var instrument = index ? "KSE 100" : alert.Symbol;
        var direction = alert.Direction == "above" ? "at or above" : "at or below";
        var reading = alert.Trigger!;
        string Value(decimal value) => index
            ? $"{value.ToString("#,0.####", CultureInfo.InvariantCulture)} points"
            : $"PKR {value.ToString("#,0.00##", CultureInfo.InvariantCulture)}";
        return $"{instrument} price alert\n{Value(reading.Value)} at {PsxTime.Now(reading.ObservedUtc):HH:mm} PKT ({reading.Source}). "
            + $"Target {direction} {Value(alert.Target)}.";
    }
}
