using System.Globalization;
using System.Text.Json;

namespace TradingAgent.Tools;

/// <summary>
/// Reads optional scalar tool arguments without caring what concrete CLR type the caller's JSON was
/// bound to. Depending on the provider and transport the same argument arrives as a
/// <see cref="JsonElement"/>, a boxed number, or a string ("60", "true"), so every accessor
/// normalizes through text and returns null for anything it cannot read — an unusable argument falls
/// back to its configured default instead of throwing mid-turn.
/// </summary>
internal static class ToolArgs
{
    public static int? Int(IReadOnlyDictionary<string, object?> args, string key) =>
        Decimal(args, key) is { } value ? (int)Math.Round(value) : null;

    public static long? Long(IReadOnlyDictionary<string, object?> args, string key) =>
        Decimal(args, key) is { } value ? (long)Math.Round(value) : null;

    public static decimal? Decimal(IReadOnlyDictionary<string, object?> args, string key)
    {
        var text = Text(args, key);
        return decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    public static bool? Bool(IReadOnlyDictionary<string, object?> args, string key) =>
        Text(args, key)?.Trim().ToLowerInvariant() switch
        {
            "true" or "yes" or "1" => true,
            "false" or "no" or "0" => false,
            _ => null
        };

    public static string? Text(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null) return null;

        var text = value is JsonElement element
            ? element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => element.ToString()
            }
            : value.ToString();

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
