namespace PageAgent.Models;

/// <summary>Structured snapshot of a page's visible structure.</summary>
public sealed class PageAnalysis
{
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public List<string> Headings { get; set; } = [];
    public List<LinkInfo> Links { get; set; } = [];

    /// <summary>Non-null when the analysis itself failed.</summary>
    public string? Error { get; set; }

    public bool IsEmpty => string.IsNullOrEmpty(Url) || Url == "about:blank";

    public override string ToString() =>
        $"[{Title}] {Url} | {Headings.Count} headings, {Links.Count} links";
}

/// <summary>A single hyperlink captured from the page DOM.</summary>
public sealed class LinkInfo
{
    public string Text { get; set; } = string.Empty;
    public string Href { get; set; } = string.Empty;

    public override string ToString() => $"\"{Text}\" → {Href}";
}
