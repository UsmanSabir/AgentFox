using System.Text;
using System.Text.RegularExpressions;

namespace AgentFox.LLM.ClaudeCode;

/// <summary>
/// Reads what <c>claude setup-token</c> prints to a terminal. Pure. Measured 2026-11-25 on CLI 2.1.258:
/// <list type="bullet">
/// <item>the token can be printed WRAPPED across lines with terminal codes between the pieces (an <c>ESC ( B</c>
/// among them), between "valid for 1 year):" and "Store this token";</item>
/// <item>words are placed with cursor moves (<c>ESC [ 26 G</c>), not spaces, so a move must read as a space, or
/// "valid for 1 year" becomes "validfor1year" and the token is never found;</item>
/// <item>with the agent's own config folder the CLI prints "Hold Shift while selecting to use your terminal's native
/// copy" between the link and the paste prompt. Joining lines glued that onto the link's <c>state</c>, and claude.ai
/// answered "Sign-in did not complete". So the link is taken from the CLI's own hyperlink (OSC 8), which carries it
/// whole and unwrapped, and only falls back to the printed text.</item>
/// </list>
/// Raw terminal output must never be logged or shown: it holds the token.
/// </summary>
public static partial class ClaudeCodeTerminalText
{
    /// <summary>Removes terminal control sequences. A cursor move reads as a space, since that is what it draws.</summary>
    public static string Strip(string raw)
    {
        var s = Osc().Replace(raw, "");
        s = CursorMove().Replace(s, " ");
        s = Csi().Replace(s, "");
        s = Charset().Replace(s, "");
        s = OtherEscape().Replace(s, "");
        return s.Replace("\r", "\n");
    }

    /// <summary>The claude.ai sign-in link from RAW output: the CLI's own hyperlink first, which is exact, and
    /// otherwise the printed text.</summary>
    public static string? FindSignInLinkInRaw(string raw)
    {
        foreach (Match h in Hyperlink().Matches(raw))
            if (IsSignInLink(h.Groups[1].Value)) return h.Groups[1].Value;
        return FindSignInLink(Strip(raw));
    }

    /// <summary>The claude.ai sign-in link from STRIPPED text, joined back together when the terminal wrapped it.</summary>
    public static string? FindSignInLink(string stripped)
    {
        var joined = Unwrapped(stripped);
        var m = Link().Match(joined);
        if (!m.Success) return null;
        var link = m.Value;
        // Whatever the CLI prints next follows the link with no space once lines are joined: the copy hint, then the
        // paste prompt. Cut at each of them.
        foreach (var next in FollowsLink)
        {
            var cut = link.IndexOf(next, StringComparison.Ordinal);
            if (cut > 0) link = link[..cut];
        }
        return IsSignInLink(link) ? link : null;
    }

    private static readonly string[] FollowsLink = ["HoldShift", "Paste"];

    /// <summary>A link whose <c>state</c> is a plain url-safe value. A link with terminal text glued on is refused
    /// rather than sent to claude.ai, which then only says "Sign-in did not complete".</summary>
    internal static bool IsSignInLink(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Host != "claude.com") return false;
        var state = uri.Query.TrimStart('?').Split('&')
            .Select(p => p.Split('=', 2)).FirstOrDefault(p => p[0] == "state");
        return state is { Length: 2 } && UrlSafe().IsMatch(state[1]);
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

    /// <summary>Masks anything shaped like a credential. For error text and logs.</summary>
    public static string Redact(string text) => AgentFox.LLM.Subscription.SecretRedaction.Redact(text);

    private static string Unwrapped(string stripped)
    {
        var sb = new StringBuilder(stripped.Length);
        foreach (var ch in stripped)
            if (!char.IsWhiteSpace(ch)) sb.Append(ch);
        return sb.ToString();
    }

    [GeneratedRegex(@"\x1b\][^\x07\x1b]*(\x07|\x1b\\)")] private static partial Regex Osc();
    [GeneratedRegex(@"\x1b\]8;[^;\x07\x1b]*;(https://claude\.com/cai/oauth/authorize\?[^\x07\x1b\s]+)(\x07|\x1b\\)")] private static partial Regex Hyperlink();
    [GeneratedRegex(@"\x1b\[[0-9;]*[ABCDEFGHdf]")] private static partial Regex CursorMove();
    [GeneratedRegex(@"\x1b\[[0-9;?<>=]*[ -/]*[@-~]")] private static partial Regex Csi();
    [GeneratedRegex(@"\x1b[()*+][A-Za-z0-9]")] private static partial Regex Charset();
    [GeneratedRegex(@"\x1b.")] private static partial Regex OtherEscape();
    [GeneratedRegex(@"https://claude\.com/cai/oauth/authorize\?[A-Za-z0-9%._~:/?#\[\]@!$&'()*+,;=-]+")] private static partial Regex Link();
    [GeneratedRegex(@"^[A-Za-z0-9_\-]{1,256}$")] private static partial Regex UrlSafe();
    [GeneratedRegex(@"valid\s+for\s+1\s+year\)\s*:?(.*?)Store", RegexOptions.Singleline)] private static partial Regex TokenBlock();
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
    [GeneratedRegex(@"^sk-ant-oat01-[A-Za-z0-9_-]{20,400}$")] private static partial Regex SetupToken();
    [GeneratedRegex(@"OAuth error:\s*([^\n]+)")] private static partial Regex OAuthError();
}
