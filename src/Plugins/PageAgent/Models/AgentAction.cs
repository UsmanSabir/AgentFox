using System.Text.Json.Serialization;

namespace PageAgent.Models;

/// <summary>
/// A structured action decided by the LLM planner.
/// Only the fields relevant to the chosen <see cref="Action"/> type need to be populated.
/// </summary>
public sealed class AgentAction
{
    /// <summary>
    /// The action to execute. One of:
    /// search | open | click | analyze | extract | done
    /// </summary>
    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    /// <summary>Search query text (used with action = "search").</summary>
    [JsonPropertyName("query")]
    public string? Query { get; set; }

    /// <summary>Full URL to navigate to (used with action = "open").</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>
    /// Visible text of the link or element to click (used with action = "click").
    /// The agent matches this against page elements using fuzzy text matching.
    /// </summary>
    [JsonPropertyName("selector")]
    public string? Selector { get; set; }

    /// <summary>Brief explanation of why this action was chosen.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    public override string ToString() =>
        $"[{Action}]{(Query != null ? $" query=\"{Query}\"" : "")}" +
        $"{(Url != null ? $" url={Url}" : "")}" +
        $"{(Selector != null ? $" selector=\"{Selector}\"" : "")}" +
        $"{(Reason != null ? $" — {Reason}" : "")}";
}
