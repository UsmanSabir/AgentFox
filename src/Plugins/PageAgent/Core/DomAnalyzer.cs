using System.Text.Json;

namespace PageAgent.Core;

/// <summary>
/// Provides JavaScript snippets that are injected into the live page via
/// PuppeteerSharp's EvaluateExpressionAsync to extract structured DOM data.
/// All scripts return JSON-encoded strings and are designed as self-contained IIFEs.
/// </summary>
internal static class DomAnalyzer
{
    private static readonly JsonSerializerOptions _jsonOpts =
        new() { PropertyNameCaseInsensitive = true };

    // ── Script generators ─────────────────────────────────────────────────────
    // We use verbatim + concatenation rather than interpolated raw strings to
    // avoid C# compiler confusion between JS object literal braces and C# interpolation.

    /// <summary>JS that serialises the top N visible headings as a JSON string array.</summary>
    internal static string HeadingsScript(int maxCount) =>
        @"JSON.stringify(
            Array.from(document.querySelectorAll('h1, h2, h3'))
                .slice(0, " + maxCount + @")
                .map(h => h.innerText.trim())
                .filter(t => t.length > 0)
        )";

    /// <summary>
    /// JS that serialises the top N absolute hyperlinks as a JSON array of
    /// <c>{ text, href }</c> objects. Filters out empty-text and non-http links.
    /// </summary>
    internal static string LinksScript(int maxCount) =>
        @"JSON.stringify(
            Array.from(document.querySelectorAll('a[href]'))
                .filter(a => {
                    const t = a.innerText.trim();
                    return t.length > 0 && a.href.startsWith('http');
                })
                .slice(0, " + maxCount + @")
                .map(a => ({
                    text: a.innerText.trim().replace(/\s+/g, ' ').substring(0, 100),
                    href: a.href
                }))
        )";

    /// <summary>
    /// JS IIFE that removes noisy elements (nav, scripts, ads) and returns visible
    /// body text, truncated to <paramref name="maxLength"/> characters.
    /// </summary>
    internal static string TextContentScript(int maxLength) =>
        @"(() => {
            const clone = document.body.cloneNode(true);
            clone.querySelectorAll(
                'script, style, noscript, nav, footer, header, aside,' +
                '[role=""banner""], [role=""navigation""], [role=""complementary""],' +
                '.ad, .ads, .advertisement, .cookie-banner, .popup'
            ).forEach(el => el.remove());

            const main = clone.querySelector(
                'main, article, [role=""main""], .content, .post,' +
                '#content, #main, .entry-content, .article-body'
            );
            const raw = ((main ?? clone).innerText ?? '').trim();
            return raw.replace(/\n{3,}/g, '\n\n').substring(0, " + maxLength + @");
        })()";

    /// <summary>
    /// JS IIFE that finds and clicks the best-matching visible link/button using
    /// cascading match strategies: exact → substring → word-level.
    /// Returns <c>"clicked:&lt;text&gt;"</c> on success or <c>"no_match"</c>.
    /// </summary>
    internal static string SmartClickScript(string targetText)
    {
        // JSON-encode the target so arbitrary characters are safely embedded in JS
        var jsLiteral = JsonSerializer.Serialize(targetText.ToLowerInvariant());

        return @"(() => {
            const target = " + jsLiteral + @";
            const candidates = Array.from(
                document.querySelectorAll('a[href], button, [role=""link""], [role=""button""]')
            ).filter(el => el.offsetParent !== null);

            let best = candidates.find(el => el.innerText.trim().toLowerCase() === target);

            if (!best)
                best = candidates.find(el => el.innerText.toLowerCase().includes(target));

            if (!best) {
                const words = target.split(/\s+/).filter(w => w.length > 3);
                if (words.length > 0)
                    best = candidates.find(el => {
                        const text = el.innerText.toLowerCase();
                        return words.some(w => text.includes(w));
                    });
            }

            if (best) {
                best.scrollIntoView({ behavior: 'instant', block: 'center' });
                best.click();
                return 'clicked:' + best.innerText.trim().replace(/\s+/g, ' ').substring(0, 80);
            }
            return 'no_match';
        })()";
    }

    // ── Deserialisation helpers ───────────────────────────────────────────────

    internal static List<string> DeserialiseStrings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json, _jsonOpts) ?? []; }
        catch { return []; }
    }

    internal static List<LinkDto> DeserialiseLinks(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<LinkDto>>(json, _jsonOpts) ?? []; }
        catch { return []; }
    }

    internal sealed class LinkDto
    {
        public string Text { get; set; } = string.Empty;
        public string Href { get; set; } = string.Empty;
    }
}
