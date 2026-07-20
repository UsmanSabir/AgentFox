using AgentFox.Plugins.Interfaces;
using System.Text.Json;
using TradingAgent.Market;

namespace TradingAgent.Tools;

/// <summary>
/// Checks whether PSX is currently open.
/// Trading hours: Monday–Friday, 09:15–15:30 Pakistan Standard Time (UTC+5).
/// Backed by <see cref="PsxMarketClock"/> so it agrees with the take-profit retry worker.
/// </summary>
public sealed class CheckMarketTool : BaseTool
{
    private readonly IMarketCalendar _calendar;

    public CheckMarketTool(IMarketCalendar calendar) => _calendar = calendar;

    public override string Name => "check_market";

    public override string Description =>
        "Check whether the PSX (Pakistan Stock Exchange) is currently open. " +
        "PSX trades Monday to Friday, 09:15 to 15:30 Pakistan Standard Time (UTC+5). " +
        "Always call this before placing an order.";

    public override Dictionary<string, ToolParameter> Parameters => new();

    protected override Task<ToolResult> ExecuteInternalAsync(
        Dictionary<string, object?> arguments)
    {
        var status = _calendar.GetStatus();

        var result = new
        {
            is_open          = status.IsOpen,
            current_time_pkt = status.PktNow.ToString("yyyy-MM-dd HH:mm:ss"),
            day              = status.PktNow.DayOfWeek.ToString(),
            reason           = status.Reason,
            next_open_pkt    = status.NextOpenPkt?.ToString("yyyy-MM-dd HH:mm:ss"),
            schedule_source  = status.ScheduleSource
        };

        return Task.FromResult(ToolResult.Ok(JsonSerializer.Serialize(result)));
    }
}
