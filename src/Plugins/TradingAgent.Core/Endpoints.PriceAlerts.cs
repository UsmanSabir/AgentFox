using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TradingAgent.Market;
using TradingAgent.Persistence;
using TradingAgent.Watchlist;

namespace TradingAgent;

public sealed partial class TradingCoreEndpoints
{
    private static void MapPriceAlertsEndpoints(RouteGroupBuilder trading)
    {
        var alerts = trading.MapGroup("/price-alerts");
        alerts.MapGet("", ListPriceAlertsAsync);
        alerts.MapPost("", CreatePriceAlertAsync).RequireAuthorization("TradingAnalyst");
        alerts.MapPut("/{id:guid}", EditPriceAlertAsync).RequireAuthorization("TradingAnalyst");
        alerts.MapDelete("/{id:guid}", CancelPriceAlertAsync).RequireAuthorization("TradingAnalyst");
    }

    public static async Task<IResult> ListPriceAlertsAsync(HttpContext http, SqliteTradingRepository repository,
        PriceAlertWorker worker, IMarketCalendar calendar, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var rules = await repository.ReadPriceAlertsAsync(ct);
        return Results.Ok(new
        {
            marketOpen = calendar.GetStatus().IsOpen,
            rules = rules.Select(rule => new { rule, latest = rule.State == "watching" ? worker.Latest(rule) : null })
        });
    }

    public static async Task<IResult> CreatePriceAlertAsync(PriceAlertInput body, SqliteTradingRepository repository, CancellationToken ct)
    {
        var alert = PriceAlert.Create(body, DateTime.UtcNow);
        if (alert is null) return InvalidPriceAlert();
        return await repository.WritePriceAlertAsync(alert, null, ct)
            ? Results.Ok(alert) : Results.Conflict(new { message = "This target is already watching or the price alert limit has been reached." });
    }

    public static async Task<IResult> EditPriceAlertAsync(Guid id, PriceAlertInput body, SqliteTradingRepository repository, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var next = PriceAlert.Create(body, now, id);
        if (next is null) return InvalidPriceAlert();
        var previous = (await repository.ReadPriceAlertsAsync(ct)).FirstOrDefault(a => a.Id == id);
        if (previous is null) return Results.NotFound();
        return previous.State == "watching" && previous.ExpiresUtc > now
            && await repository.WritePriceAlertAsync(next, previous, ct)
            ? Results.Ok(next) : Results.Conflict(new { message = "This alert is no longer watching." });
    }

    public static async Task<IResult> CancelPriceAlertAsync(Guid id, SqliteTradingRepository repository, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var previous = (await repository.ReadPriceAlertsAsync(ct)).FirstOrDefault(a => a.Id == id);
        if (previous is null) return Results.NotFound();
        if (previous.State == "cancelled") return Results.Ok(previous);
        var next = previous with { State = "cancelled", FinishedUtc = now };
        return previous.State == "watching" && previous.ExpiresUtc > now
            && await repository.WritePriceAlertAsync(next, previous, ct)
            ? Results.Ok(next) : Results.Conflict(new { message = "This alert is no longer watching." });
    }

    private static IResult InvalidPriceAlert() =>
        Results.BadRequest(new { message = "Choose a PSX symbol or KSE 100, a direction, and a positive target with up to four decimal places." });
}
