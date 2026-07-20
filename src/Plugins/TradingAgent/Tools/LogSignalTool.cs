using AgentFox.Plugins.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using TradingAgent.Config;

namespace TradingAgent.Tools;

/// <summary>
/// Records every detected trading signal to a daily JSONL file and via ILogger.
/// Call this for every signal regardless of whether an order was placed.
/// </summary>
public sealed class LogSignalTool : BaseTool
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly IOptions<AhkConfig> _ahkConfig;
    private readonly ILogger<LogSignalTool> _logger;

    public override string Name => "log_signal";

    public override string Description =>
        "Save a detected trading signal and its outcome to the daily log file. " +
        "Always call this for every signal found, whether executed or not.";

    public override Dictionary<string, ToolParameter> Parameters => new()
    {
        ["action"]           = new() { Type = "string",  Description = "BUY, SELL, HOLD, or UNKNOWN",              Required = true  },
        ["symbol"]           = new() { Type = "string",  Description = "PSX ticker symbol",                         Required = false },
        ["entry_price"]      = new() { Type = "number",  Description = "Entry/limit price in PKR",                  Required = false },
        ["target"]           = new() { Type = "number",  Description = "Take-profit target price",                  Required = false },
        ["stop_loss"]        = new() { Type = "number",  Description = "Stop-loss price",                           Required = false },
        ["confidence"]       = new() { Type = "string",  Description = "HIGH, MEDIUM, LOW, or NONE",                Required = true  },
        ["sender"]           = new() { Type = "string",  Description = "WhatsApp sender identifier",               Required = false },
        ["raw_message"]      = new() { Type = "string",  Description = "Original message text",                     Required = false },
        ["executed"]         = new() { Type = "boolean", Description = "Whether an order was placed",               Required = true  },
        ["execution_reason"] = new() { Type = "string",  Description = "Why the trade was or was not executed",     Required = false },
    };

    public LogSignalTool(IOptions<AhkConfig> ahkConfig, ILogger<LogSignalTool> logger)
    {
        _ahkConfig = ahkConfig;
        _logger    = logger;
    }

    protected override async Task<ToolResult> ExecuteInternalAsync(
        Dictionary<string, object?> arguments)
    {
        var entry = new SignalLogEntry
        {
            TimestampUtc    = DateTime.UtcNow,
            Action          = arguments.GetValueOrDefault("action")?.ToString() ?? "UNKNOWN",
            Symbol          = arguments.GetValueOrDefault("symbol")?.ToString() ?? "",
            EntryPrice      = ToDecimal(arguments.GetValueOrDefault("entry_price")),
            Target          = ToDecimal(arguments.GetValueOrDefault("target")),
            StopLoss        = ToDecimal(arguments.GetValueOrDefault("stop_loss")),
            Confidence      = arguments.GetValueOrDefault("confidence")?.ToString() ?? "NONE",
            Sender          = arguments.GetValueOrDefault("sender")?.ToString() ?? "",
            RawMessage      = arguments.GetValueOrDefault("raw_message")?.ToString() ?? "",
            Executed        = Convert.ToBoolean(arguments.GetValueOrDefault("executed") ?? false),
            ExecutionReason = arguments.GetValueOrDefault("execution_reason")?.ToString() ?? ""
        };

        _logger.LogInformation(
            "[Signal] {Action} {Symbol} @ {Price:N2} | Target={Target:N2} SL={SL:N2} " +
            "| Conf={Confidence} | Exec={Executed} | {Reason}",
            entry.Action, entry.Symbol, entry.EntryPrice,
            entry.Target, entry.StopLoss,
            entry.Confidence, entry.Executed, entry.ExecutionReason);

        var logDir  = _ahkConfig.Value.LogDir;
        var logPath = Path.Combine(logDir, $"signals_{DateTime.UtcNow:yyyyMMdd}.jsonl");

        try
        {
            Directory.CreateDirectory(logDir);
            await File.AppendAllTextAsync(logPath,
                JsonSerializer.Serialize(entry, _jsonOptions) + Environment.NewLine);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[LogSignal] Failed to write to {Path}.", logPath);
            return ToolResult.Fail($"Failed to write log: {ex.Message}");
        }

        return ToolResult.Ok(JsonSerializer.Serialize(new { success = true, log_path = logPath }));
    }

    private static decimal? ToDecimal(object? value)
    {
        if (value is null) return null;
        try { return Convert.ToDecimal(value); }
        catch { return null; }
    }

    private sealed class SignalLogEntry
    {
        public DateTime TimestampUtc    { get; set; }
        public string   Action          { get; set; } = "";
        public string   Symbol          { get; set; } = "";
        public decimal? EntryPrice      { get; set; }
        public decimal? Target          { get; set; }
        public decimal? StopLoss        { get; set; }
        public string   Confidence      { get; set; } = "";
        public string   Sender          { get; set; } = "";
        public string   RawMessage      { get; set; } = "";
        public bool     Executed        { get; set; }
        public string   ExecutionReason { get; set; } = "";
    }
}
