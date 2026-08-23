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

        Return EXACTLY this JSON shape — ALWAYS the wrapper object, even for one tip or none:
        {
          "signals": [
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
          ]
        }

        ONE array element PER named stock. A single message often carries SEVERAL tips
        (e.g. "OGDC ... PSO ...") — emit one object for each. A message with no tradeable tip → "signals": [].

        FIRST decide, for each candidate stock, whether it is a signal. Only an ACTIONABLE per-stock instruction is a signal.

        is_signal = TRUE only when BOTH are present:
          1. a clear action on a SPECIFIC named PSX stock — buy / accumulate / add → BUY,
             sell / book profit / exit / niklo → SELL, and
          2. a recognizable PSX ticker or company name (see list below).

        is_signal = FALSE (set action="UNKNOWN", confidence="NONE", symbol="") for everything else:
          - Market / index OUTLOOK or commentary: KSE-100 levels, "important resistance level",
            "support area", "bullish/bearish points", index targets. Index points (large 4–6 digit
            numbers like 182500, 175185, 173000) are NOT share prices — never read them as entry/target.
          - News, announcements, official statements, summits, MoUs, agreements, articles, forwards.
          - Image/document/link captions with no explicit stock action.
          - Greetings, questions, opinions, P&L chat, or anything lacking a buy/sell on a named stock.

        When in doubt, is_signal=FALSE. Skipping a borderline message is far safer than trading on noise.

        Range & quantity handling (PSX tips usually give zones, not exact numbers):
        - entry_price: if a buy/accumulation RANGE is given (e.g. "accumulate around 20.5-21",
          "buy 20.5 to 21"), set entry_price to the UPPER bound (21). For a single number, use it as-is.
          This becomes the limit price, so the upper bound guarantees a fill anywhere in the zone.
        - target: if multiple targets or a target RANGE is given (e.g. "Targets: 22.50 - 24.50"),
          set target to the FIRST/LOWER target (22.50) — the nearest, most-likely-to-fill take-profit.
        - quantity: leave null UNLESS the tip states an explicit share count (e.g. "buy 500 shares").
          A missing quantity is normal and does NOT lower confidence — the executor sizes the position
          from the configured per-stock budget.

        Confidence rules (a missing entry price does NOT lower confidence — it is resolved at execution):
        - HIGH:   a clear action (buy/accumulate or sell/book-profit) + a specific PSX stock are present
        - MEDIUM: a stock is named and buy/sell is implied, but the action wording is not explicit
        - LOW:    a stock is named but there is no action at all
        - NONE:   not a trading signal

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
        "Parse a WhatsApp message and extract ALL structured PSX trading signals it contains. " +
        "Returns { is_signal, count, signals[] } — one entry per named stock, each with action, " +
        "symbol, entry price, target, stop-loss, order type, and confidence. A message may hold " +
        "several tips (split automatically) or none (signals is empty — discard the message). " +
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

        var parsed = ParseSignals(rawJson);
        if (parsed.Count == 0)
            _logger.LogWarning("[ParseSignal] No signals parsed from response: {Json}", rawJson);

        // Deterministic backstop, applied PER candidate. The classifier runs on a cheap local model that
        // occasionally flags market-outlook/news/chatter as a tradeable tip. A real, executable per-stock
        // signal needs BOTH a concrete BUY/SELL action AND a ticker — so anything missing either (index
        // commentary with no symbol, a HOLD, news) is forced to a non-signal here, regardless of what the
        // LLM returned. This is the layer the workflow trusts to "discard non-tip messages"; it can only
        // ever discard, never promote, so it cannot turn noise into an order.
        var actionable = new List<TradingSignal>();
        foreach (var signal in parsed)
        {
            signal.RawMessage = message;
            signal.Symbol = (signal.Symbol ?? "").Trim().ToUpperInvariant();
            signal.Action = (signal.Action ?? "UNKNOWN").Trim().ToUpperInvariant();

            var hasAction = signal.Action is "BUY" or "SELL";
            var hasSymbol = !string.IsNullOrWhiteSpace(signal.Symbol);
            if (!signal.IsSignal || !hasAction || !hasSymbol)
            {
                _logger.LogInformation(
                    "[ParseSignal] Discarded candidate (is_signal={IsSignal} action='{Action}' symbol='{Symbol}') — " +
                    "not a per-stock BUY/SELL tip.",
                    signal.IsSignal, signal.Action, signal.Symbol);
                continue;
            }

            actionable.Add(signal);
            _logger.LogInformation(
                "[ParseSignal] Signal: {Action} {Symbol} entry={Entry} target={Target} conf={Confidence} | {Reason}",
                signal.Action, signal.Symbol, signal.EntryPrice, signal.Target,
                signal.Confidence, signal.ConfidenceReason);
        }

        // Project each actionable signal to the snake_case shape the order tools/agent consume.
        var projected = actionable.Select(s => new
        {
            action            = s.Action,
            symbol            = s.Symbol,
            entry_price       = s.EntryPrice,
            target            = s.Target,
            stop_loss         = s.StopLoss,
            quantity          = s.Quantity,
            order_type        = s.OrderType,
            confidence        = s.Confidence,
            confidence_reason = s.ConfidenceReason
        });

        return ToolResult.Ok(JsonSerializer.Serialize(new
        {
            is_signal = actionable.Count > 0,
            count     = actionable.Count,
            signals   = projected
        }, _snakeOptions));
    }

    /// <summary>
    /// Tolerantly turns the model's JSON into a list of candidate signals. Accepts the documented
    /// wrapper <c>{ "signals": [ ... ] }</c>, a bare array <c>[ ... ]</c>, or a single object
    /// <c>{ ... }</c> (older shape). Returns an empty list when nothing usable is found.
    /// </summary>
    private static List<TradingSignal> ParseSignals(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return new();

        // 1) Wrapper object { "signals": [...] }
        try
        {
            var wrap = JsonSerializer.Deserialize<ParseResult>(rawJson, _snakeOptions);
            if (wrap?.Signals is not null) return wrap.Signals;
        }
        catch (JsonException) { /* try the next shape */ }

        // 2) Bare array [...]
        try
        {
            var arr = JsonSerializer.Deserialize<List<TradingSignal>>(rawJson, _snakeOptions);
            if (arr is not null) return arr;
        }
        catch (JsonException) { /* try the next shape */ }

        // 3) Single object {...}
        try
        {
            var one = JsonSerializer.Deserialize<TradingSignal>(rawJson, _snakeOptions);
            if (one is not null) return new() { one };
        }
        catch (JsonException) { /* give up */ }

        return new();
    }

    private sealed class ParseResult
    {
        public List<TradingSignal>? Signals { get; set; }
    }

    /// <summary>
    /// Normalises an LLM response into a bare JSON value. Strips a surrounding markdown code fence
    /// (```json ... ```) and any prose before/after by slicing to the outermost JSON object OR array —
    /// whichever opens first ('{'/'['). Returns the input unchanged when no JSON delimiters are found
    /// (the caller's tolerant deserialize then yields no signals).
    /// </summary>
    private static string StripJsonFence(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        // Choose the delimiter pair for whichever value opens first.
        var firstObj = text.IndexOf('{');
        var firstArr = text.IndexOf('[');

        var useArray = firstArr >= 0 && (firstObj < 0 || firstArr < firstObj);
        var start    = useArray ? firstArr : firstObj;
        var end      = useArray ? text.LastIndexOf(']') : text.LastIndexOf('}');

        return start >= 0 && end > start
            ? text.Substring(start, end - start + 1)
            : text.Trim();
    }
}
