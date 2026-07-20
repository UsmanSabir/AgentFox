using AgentFox.Plugins.Research;

namespace AgentFox.Plugins.Models;

/// <summary>The result of an agent turn as seen by the web/API layer: the reply text plus any
/// research source references collected during the turn.</summary>
public sealed class AgentReply
{
    public string Output { get; set; } = string.Empty;
    public List<ResearchReference> References { get; set; } = new();
}
