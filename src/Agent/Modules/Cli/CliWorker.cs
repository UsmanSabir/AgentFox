using AgentFox.Skills;
using Microsoft.Extensions.Hosting;

namespace AgentFox.Modules.Cli;

public class CliWorker : BackgroundService
{
    private readonly IHostApplicationLifetime _lifetime;

    public CliWorker(
        IHostApplicationLifetime lifetime)
    {
        _lifetime = lifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Console.IsInputRedirected) // || !Console.KeyAvailable)
        {
            return;
        }

//        Console.WriteLine("CLI Mode Started");
        await Task.Run(async () =>
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var input = Console.ReadLine();

                if (string.Equals(input, "exit", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("Shutting down...");

                    _lifetime.StopApplication();
                    break;
                }
                //TODO
                //var response = await _agent.RunAsync(input);
                //Console.WriteLine(response);
            }
        });

    }
}