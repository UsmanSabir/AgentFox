namespace AgentFox.Plugins.Interfaces;

internal interface IAgentService
{
    Task<string> RunAsync(string input);
}