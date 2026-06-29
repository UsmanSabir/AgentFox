using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using TradingAgent.Config;
using TradingAgent.Models;

namespace TradingAgent.Trading;

/// <summary>
/// Thread-safe, disk-backed queue of take-profit SELLs awaiting retry. Persisted to
/// <c>{LogDir}/pending_take_profits.json</c> so pending exits survive an app restart — a take-profit
/// must never be silently dropped just because the process bounced. All mutations are serialized and
/// flushed to disk immediately.
/// </summary>
public sealed class PendingTakeProfitStore
{
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    // Failure messages that mean "try again later" (the buy hasn't settled/filled yet) rather than a
    // permanent rejection. Only retryable failures are queued; a hard reject is reported and dropped.
    private static readonly string[] _retryableMarkers =
    [
        "insufficient", "exposure", "not enough", "no position", "holding", "margin",
        "settle", "unsettled", "available", "balance", "try again", "no share"
    ];

    private readonly object _lock = new();
    private readonly string _path;
    private readonly ILogger<PendingTakeProfitStore> _logger;
    private readonly List<PendingTakeProfit> _items;

    public PendingTakeProfitStore(
        IOptions<AhkConfig> ahkConfig, IConfiguration configuration, ILogger<PendingTakeProfitStore> logger)
    {
        _logger = logger;

        var root   = ComputeWorkspaceRoot(configuration);
        var logDir = ahkConfig.Value.LogDir;
        var dir    = Path.IsPathRooted(logDir) ? logDir : Path.Combine(root, logDir);
        Directory.CreateDirectory(dir);

        _path  = Path.Combine(dir, "pending_take_profits.json");
        _items = Load();

        if (_items.Count > 0)
            _logger.LogInformation("[TakeProfit] Loaded {Count} pending take-profit sell(s) from disk.", _items.Count);
    }

    /// <summary>True when a failed-sell message indicates a transient (retryable) condition.</summary>
    public static bool IsRetryable(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        var lower = message.ToLowerInvariant();
        return _retryableMarkers.Any(lower.Contains);
    }

    /// <summary>
    /// Queues a take-profit retry, unless an identical pending one (same symbol + price) already exists.
    /// Returns true if a new entry was added. <paramref name="firstDelayMinutes"/> sets the first retry.
    /// </summary>
    public bool Schedule(string symbol, int quantity, decimal targetPrice, int firstDelayMinutes, string rawMessage = "")
    {
        symbol = (symbol ?? "").Trim().ToUpperInvariant();
        if (symbol.Length == 0 || quantity <= 0 || targetPrice <= 0) return false;

        lock (_lock)
        {
            if (_items.Any(i => i.Symbol == symbol && i.TargetPrice == targetPrice && i.Quantity == quantity))
            {
                _logger.LogInformation("[TakeProfit] A pending sell for {Symbol} x{Qty} @ {Price} already queued — not duplicating.",
                    symbol, quantity, targetPrice);
                return false;
            }

            _items.Add(new PendingTakeProfit
            {
                Symbol         = symbol,
                Quantity       = quantity,
                TargetPrice    = targetPrice,
                NextAttemptUtc = DateTime.UtcNow.AddMinutes(Math.Max(0, firstDelayMinutes)),
                RawMessage     = rawMessage
            });
            Save();
            _logger.LogInformation("[TakeProfit] Queued retry: SELL {Symbol} x{Qty} @ {Price}.", symbol, quantity, targetPrice);
            return true;
        }
    }

    /// <summary>Returns the pending sells whose next-attempt time has arrived (copy; safe to iterate).</summary>
    public IReadOnlyList<PendingTakeProfit> GetDue(DateTime nowUtc)
    {
        lock (_lock)
            return _items.Where(i => i.NextAttemptUtc <= nowUtc).ToList();
    }

    public int Count { get { lock (_lock) return _items.Count; } }

    public void Remove(string id)
    {
        lock (_lock)
        {
            if (_items.RemoveAll(i => i.Id == id) > 0) Save();
        }
    }

    /// <summary>Records a failed attempt: bumps the count and pushes the next attempt out by the interval.</summary>
    public void RecordFailure(string id, string error, int retryIntervalMinutes)
    {
        lock (_lock)
        {
            var item = _items.FirstOrDefault(i => i.Id == id);
            if (item is null) return;
            item.Attempts++;
            item.LastError      = error ?? "";
            item.NextAttemptUtc = DateTime.UtcNow.AddMinutes(Math.Max(1, retryIntervalMinutes));
            Save();
        }
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    private List<PendingTakeProfit> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new();
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<List<PendingTakeProfit>>(json) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[TakeProfit] Could not load pending sells from {Path} — starting empty.", _path);
            return new();
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_items, _json));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[TakeProfit] Could not persist pending sells to {Path}.", _path);
        }
    }

    /// <summary>Mirrors the host/broker workspace-root rule so LogDir resolves to the same place.</summary>
    private static string ComputeWorkspaceRoot(IConfiguration configuration)
    {
        var first = configuration.GetSection("Workspaces").Get<string[]>()
            ?.FirstOrDefault(w => !string.IsNullOrWhiteSpace(w));

        return string.IsNullOrWhiteSpace(first) ? AppContext.BaseDirectory : Path.GetFullPath(first);
    }
}
