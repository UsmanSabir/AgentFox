using System.Text;
using System.Text.RegularExpressions;

namespace AgentFox.LLM.ClaudeCode;

/// <summary>
/// Reads what <c>claude setup-token</c> prints to a terminal. Pure. Measured 2026-11-25 on CLI 2.1.258:
/// the token is printed WRAPPED across lines with terminal codes between the pieces (an <c>ESC ( B</c> among them),
/// between "valid for 1 year):" and "Store this token". Raw terminal output must never be logged or shown: it holds
/// the token.
/// </summary>
public static partial class ClaudeCodeTerminalText
{
    /// <summary>Removes terminal control sequences.</summary>
    public static string Strip(string raw)
    {
        var s = Osc().Replace(raw, "");
        s = Csi().Replace(s, "");
        s = Charset().Replace(s, "");
        s = OtherEscape().Replace(s, "");
        return s.Replace("\r", "\n");
    }

    /// <summary>The claude.ai sign-in link, joined back together when the terminal wrapped it.</summary>
    public static string? FindSignInLink(string stripped)
    {
        var joined = Unwrapped(stripped);
        var m = Link().Match(joined);
        if (!m.Success) return null;
        var link = m.Value;
        // The paste prompt follows the link with no space once lines are joined.
        var cut = link.IndexOf("Paste", StringComparison.Ordinal);
        return cut > 0 ? link[..cut] : link;
    }

    /// <summary>True once the CLI is waiting for the code.</summary>
    public static bool IsAwaitingCode(string stripped) =>
        Unwrapped(stripped).Contains("Pastecodehere", StringComparison.OrdinalIgnoreCase);

    /// <summary>The token, when it has been printed and is well formed.</summary>
    public static string? FindToken(string stripped)
    {
        var m = TokenBlock().Match(stripped);
        if (!m.Success) return null;
        var token = Whitespace().Replace(m.Groups[1].Value, "");
        return IsWellFormedToken(token) ? token : null;
    }

    /// <summary>An error the sign-in reported (e.g. "OAuth error: Socket is closed"), without any token text.</summary>
    public static string? FindError(string stripped)
    {
        var m = OAuthError().Match(stripped);
        if (!m.Success) return null;
        var text = m.Groups[1].Value.Trim();
        return text.Length > 160 ? text[..160] : text;
    }

    public static bool IsWellFormedToken(string token) => SetupToken().IsMatch(token);

    /// <summary>Masks anything shaped like a token. For error text and logs.</summary>
    public static string Redact(string text) => AnyToken().Replace(text, "sk-ant-****");

    private static string Unwrapped(string stripped)
    {
        var sb = new StringBuilder(stripped.Length);
        foreach (var ch in stripped)
            if (!char.IsWhiteSpace(ch)) sb.Append(ch);
        return sb.ToString();
    }

    [GeneratedRegex(@"\x1b\][^\x07\x1b]*(\x07|\x1b\\)")] private static partial Regex Osc();
    [GeneratedRegex(@"\x1b\[[0-9;?<>=]*[ -/]*[@-~]")] private static partial Regex Csi();
    [GeneratedRegex(@"\x1b[()*+][A-Za-z0-9]")] private static partial Regex Charset();
    [GeneratedRegex(@"\x1b.")] private static partial Regex OtherEscape();
    [GeneratedRegex(@"https://claude\.com/cai/oauth/authorize\?[A-Za-z0-9%._~:/?#\[\]@!$&'()*+,;=-]+")] private static partial Regex Link();
    [GeneratedRegex(@"valid for 1 year\)\s*:?(.*?)Store", RegexOptions.Singleline)] private static partial Regex TokenBlock();
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
    [GeneratedRegex(@"^sk-ant-oat01-[A-Za-z0-9_-]{20,400}$")] private static partial Regex SetupToken();
    [GeneratedRegex(@"sk-ant-[A-Za-z0-9_-]+")] private static partial Regex AnyToken();
    [GeneratedRegex(@"OAuth error:\s*([^\n]+)")] private static partial Regex OAuthError();
}
