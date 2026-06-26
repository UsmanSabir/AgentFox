using AgentFox.Plugins.Interfaces;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using TradingAgent.Models;

namespace TradingAgent.Tools;

/// <summary>
/// AI-powered signal extractor.
///
/// Sends the raw WhatsApp message to the LLM with a tightly scoped extraction
/// prompt and parses the JSON response into a TradingSignal.
///
/// The LLM assesses confidence (HIGH/MEDIUM/LOW/NONE) based on which fields
/// it could unambiguously extract, and returns a confidence_reason string.
///
/// Note: uses the default IChatClient from DI. ParserModelKey in config is
/// reserved for when IModelClientFactory is added to AgentFox.Plugins.
/// </summary>
public sealed class ParseSignalTool : BaseTool
{
    private readonly IChatClient _chatClient;
    private readonly ILogger<ParseSignalTool> _logger;

    private static readonly JsonSerializerOptions _snakeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    private const string ExtractionSystemPrompt = """
        You are a PSX (Pakistan Stock Exchange) trading signal parser.
        Extract structured signals from WhatsApp group messages and return JSON only — no markdown, no explanation.

        Return exactly this JSON shape:
        {
          "is_signal":         bool,
          "action":            "BUY" | "SELL" | "HOLD" | "UNKNOWN",
          "symbol":            string,
          "entry_price":       number | null,
          "target":            number | null,
          "stop_loss":         number | null,
          "quantity":          number | null,
          "order_type":        "LIMIT" | "MARKET",
          "confidence":        "HIGH" | "MEDIUM" | "LOW" | "NONE",
          "confidence_reason": string
        }

        Confidence rules:
        - HIGH:   action + symbol + entry price all clearly and unambiguously present
        - MEDIUM: action + symbol clearly present, entry price inferred or absent
        - LOW:    symbol present but action is unclear or absent
        - NONE:   not a trading signal at all

        PSX symbols (non-exhaustive):
        OGDC PPL MARI POL PSO HBL UBL MCB ABL NBP MEBL BAFL LUCK DGKC MLCF
        PIOC CHCC FCCL EFERT FFC FFBL ENGRO FATIMA HUBC KAPCO TRG SYS NETSOL
        SEARL GLAXO ABOT ASTL ISL MUGHAL EPCL ICI LOTCHEM PAKT

        Urdu action words:  kharido / lo → BUY   |   becho / niklo → SELL

        Company name aliases:
          Lucky / Lucky Cement   → LUCK
          Oil Gas / OGDC         → OGDC
          Habib Bank             → HBL
          United Bank            → UBL
          Muslim Commercial      → MCB
          Hub Power              → HUBC
          Systems Limited        → SYS
          Maple Leaf             → MLCF
          Fauji                  → FFC
        """;

    public override string Name => "parse_signal";

    public override string Description =>
        "Parse a WhatsApp message and extract a structured PSX trading signal. " +
        "Returns action, symbol, entry price, target, stop-loss, order type, " +
        "confidence (HIGH/MEDIUM/LOW/NONE), and a one-sentence confidence reason. " +
        "Always call this first on any message that might contain a trading tip.";

    public override Dictionary<string, ToolParameter> Parameters => new()
    {
        ["message"] = new()
        {
            Type        = "string",
            Description = "The raw WhatsApp message text to parse.",
            Required    = true
        }
    };

    public ParseSignalTool(IChatClient chatClient, ILogger<ParseSignalTool> logger)
    {
        _chatClient = chatClient;
        _logger = logger;
    }

    protected override async Task<ToolResult> ExecuteInternalAsync(
        Dictionary<string, object?> arguments)
    {
        var message = arguments.GetValueOrDefault("message")?.ToString();
        if (string.IsNullOrWhiteSpace(message))
            return ToolResult.Fail("Parameter 'message' is required.");

        _logger.LogDebug("[ParseSignal] Parsing message: {Message}", message);

        string rawJson;
        try
        {
            // NOTE: No ResponseFormat is set. The default IChatClient is whatever the host's "LLM"
            // section resolves to (Ollama, OpenAI-compatible, etc.), and not every endpoint accepts
            // response_format: {"type":"json_object"} — Docker Model Runner, for example, returns 400
            // ("must be 'json_schema' or 'text'"). The extraction prompt already constrains output to
            // raw JSON; StripJsonFence + the tolerant deserialize below handle any stray formatting.
            var response = await _chatClient.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, ExtractionSystemPrompt),
                    new ChatMessage(ChatRole.User, message)
                ]);

            rawJson = StripJsonFence(response.Text ?? "");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ParseSignal] LLM call failed.");
            return ToolResult.Fail($"Signal parsing failed: {ex.Message}");
        }

        TradingSignal signal;
        try
        {
            signal = JsonSerializer.Deserialize<TradingSignal>(rawJson, _snakeOptions)
                     ?? new TradingSignal { IsSignal = false };
        }
        catch (JsonException)
        {
            _logger.LogWarning("[ParseSignal] Could not deserialize LLM response: {Json}", rawJson);
            signal = new TradingSignal { IsSignal = false };
        }

        signal.RawMessage = message;

        _logger.LogInformation(
            "[ParseSignal] is_signal={IsSignal} action={Action} symbol={Symbol} conf={Confidence} | {Reason}",
            signal.IsSignal, signal.Action, signal.Symbol,
            signal.Confidence, signal.ConfidenceReason);

        return ToolResult.Ok(JsonSerializer.Serialize(signal, _snakeOptions));
    }

    /// <summary>
    /// Normalises an LLM response into a bare JSON object. Strips a surrounding
    /// markdown code fence (```json ... ```) and any prose before/after the object
    /// by slicing to the outermost '{' .. '}'. Returns the input unchanged when no
    /// braces are found (the caller's deserialize then falls back to is_signal=false).
    /// </summary>
    private static string StripJsonFence(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var start = text.IndexOf('{');
        var end   = text.LastIndexOf('}');

        return start >= 0 && end > start
            ? text.Substring(start, end - start + 1)
            : text.Trim();
    }
}
