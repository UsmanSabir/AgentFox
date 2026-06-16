namespace TradingAgent.Config;

public class TradingAgentOptions
{
    public const string SectionName = "TradingAgent";

    public bool AutoExecute { get; set; } = false;

    // HIGH, MEDIUM, or LOW
    public string MinConfidence { get; set; } = "HIGH";

    // Reserved: will resolve via IModelClientFactory once added to AgentFox.Plugins.
    // Currently the default IChatClient (from DI) is used for signal parsing.
    public string ParserModelKey { get; set; } = "CheapModel";

    public int DuplicateWindowMinutes { get; set; } = 60;
}
