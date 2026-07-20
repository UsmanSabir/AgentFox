namespace AgentFox.Plugins.Interfaces;

/// <summary>
/// Optional extension for <see cref="IAppModule"/> implementations that need
/// access to the live agent context after it has been fully initialized.
/// <para>
/// Implement this alongside <see cref="IAppModule"/> to receive an
/// <see cref="IPluginContext"/> which allows the plugin to:
/// register tools dynamically, contribute to the system prompt each turn,
/// subscribe to tool/skill lifecycle hooks, and read conversation history.
/// </para>
/// <example>
/// <code>
/// public class MyPlugin : IAgentAwareModule
/// {
///     public string Name => "my-plugin";
///     public void RegisterServices(IServiceCollection s, IConfiguration c) { }
///     public void MapEndpoints(IEndpointRouteBuilder e) { }
///     public Task StartAsync(IServiceProvider s) => Task.CompletedTask;
///
///     public Task OnAgentReadyAsync(IPluginContext ctx)
///     {
///         ctx.RegisterTool(new MyCustomTool());
///         ctx.ContributeToSystemPrompt("my-plugin", () => $"Current time: {DateTime.UtcNow:u}");
///         ctx.OnToolPreExecute((name, args, id) => { Console.WriteLine($"Tool: {name}"); return Task.CompletedTask; });
///         return Task.CompletedTask;
///     }
/// }
/// </code>
/// </example>
/// </summary>
public interface IAgentAwareModule : IAppModule
{
    /// <summary>
    /// Called once after the primary <see cref="FoxAgent"/> is built and ready.
    /// Use <paramref name="context"/> to register tools, inject prompt fragments,
    /// subscribe to hooks, or read conversation history.
    /// </summary>
    Task OnAgentReadyAsync(IPluginContext context);
}
