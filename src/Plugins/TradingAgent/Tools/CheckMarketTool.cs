using AgentFox.Plugins.Interfaces;
using System.Text.Json;

namespace TradingAgent.Tools;

/// <summary>
/// Checks whether PSX is currently open.
/// Trading hours: Monday–Friday, 09:15–15:30 Pakistan Standard Time (UTC+5).
/// </summary>
public sealed class CheckMarketTool : BaseTool
{
    // Pakistan Standard Time: UTC+5, no DST.
    // Windows ID: "Pakistan Standard Time"  |  IANA: "Asia/Karachi"
    private static readonly TimeZoneInfo _pkt = ResolvePkt();

    public override string Name => "check_market";

    public override string Description =>
        "Check whether the PSX (Pakistan Stock Exchange) is currently open. " +
        "PSX trades Monday to Friday, 09:15 to 15:30 Pakistan Standard Time (UTC+5). " +
        "Always call this before placing an order.";

    public override Dictionary<string, ToolParameter> Parameters => new();

    protected override Task<ToolResult> ExecuteInternalAsync(
        Dictionary<string, object?> arguments)
    {
        var pktNow     = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _pkt);
        var isWeekday  = pktNow.DayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Friday;
        var currentTod = TimeOnly.FromDateTime(pktNow);
        var open       = new TimeOnly(9, 15);
        var close      = new TimeOnly(15, 30);
        var inHours    = currentTod >= open && currentTod <= close;
        var isOpen     = isWeekday && inHours;

        var reason = isOpen
            ? $"PSX is open. Current time: {currentTod:HH:mm} PKT."
            : !isWeekday
                ? $"PSX is closed on {pktNow.DayOfWeek}s."
                : currentTod < open
                    ? $"Pre-market. Opens at 09:15 PKT. Current: {currentTod:HH:mm} PKT."
                    : $"After-hours. Closed at 15:30 PKT. Current: {currentTod:HH:mm} PKT.";

        var result = new
        {
            is_open          = isOpen,
            current_time_pkt = pktNow.ToString("yyyy-MM-dd HH:mm:ss"),
            day              = pktNow.DayOfWeek.ToString(),
            reason
        };

        return Task.FromResult(ToolResult.Ok(JsonSerializer.Serialize(result)));
    }

    private static TimeZoneInfo ResolvePkt()
    {
        foreach (var id in new[] { "Pakistan Standard Time", "Asia/Karachi" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
        }
        // Fallback: fixed UTC+5 offset
        return TimeZoneInfo.CreateCustomTimeZone("PKT", TimeSpan.FromHours(5), "PKT", "PKT");
    }
}
