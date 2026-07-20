using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace TradingAgent.Safety;

/// <summary>
/// Guards against acting on the same WhatsApp message twice within a rolling time window.
/// Hash is computed over the normalized message text (trimmed, lowercased).
/// </summary>
public sealed class DuplicateSignalFilter
{
    private readonly ConcurrentDictionary<string, DateTime> _seen = new();
    private readonly TimeSpan _window;

    public DuplicateSignalFilter(TimeSpan window)
    {
        _window = window;
    }

    /// <summary>
    /// Returns true if an identical message was already seen within the window.
    /// Registers the message if it is new.
    /// </summary>
    public bool IsDuplicate(string messageText)
    {
        Evict();
        var hash = Hash(messageText);
        var now = DateTime.UtcNow;

        if (_seen.TryGetValue(hash, out var seenAt) && now - seenAt <= _window)
            return true;

        _seen[hash] = now;
        return false;
    }

    private void Evict()
    {
        var cutoff = DateTime.UtcNow - _window;
        foreach (var key in _seen.Keys)
        {
            if (_seen.TryGetValue(key, out var ts) && ts < cutoff)
                _seen.TryRemove(key, out _);
        }
    }

    private static string Hash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text.Trim().ToLowerInvariant()));
        return Convert.ToHexString(bytes)[..16];
    }
}
