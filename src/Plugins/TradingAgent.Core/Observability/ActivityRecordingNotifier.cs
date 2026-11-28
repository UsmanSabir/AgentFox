using AgentFox.Plugins.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace TradingAgent.Observability;

/// <summary>
/// Wraps the host's <see cref="IUserNotifier"/> so that every message sent to the person's channels (WhatsApp, Telegram,
/// Discord) also appears in today's Activity as a "Notified" row, with how many channels took it. Core's own alerts
/// (executions, stops, price alerts) and an edition's go through the one notifier, so one wrapper sees them all.
///
/// <para>
/// It records and never decides: delivery, routing and the channel's own result are the inner notifier's, returned
/// unchanged, and a failure to record can never fail a send. The text is the message itself, first line as the row and
/// the rest as its detail — what the person was told, not a summary of it. It carries no stock, because the notifier's
/// contract names none, and guessing one from prose would put the wrong name in a column people click.
/// </para>
/// </summary>
public sealed class ActivityRecordingNotifier(IUserNotifier inner, TradingActivityLog activity) : IUserNotifier
{
    public Task<int> NotifyAsync(string message, CancellationToken ct = default) => NotifyAsync(message, null, ct);

    public async Task<int> NotifyAsync(string message, string? topic, CancellationToken ct = default)
    {
        int sent;
        try { sent = await inner.NotifyAsync(message, topic, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Record(message, ActivityLevel.Warn, $"Not delivered: {ex.Message}");
            throw;
        }
        Record(message, ActivityLevel.Info, sent > 0 ? $"Sent to {sent} {(sent == 1 ? "channel" : "channels")}" : "No channel accepted it");
        return sent;
    }

    public Task<bool> WaitUntilReadyAsync(TimeSpan timeout, CancellationToken ct = default) => inner.WaitUntilReadyAsync(timeout, ct);

    private void Record(string message, ActivityLevel level, string delivery)
    {
        try
        {
            var (head, tail) = Split(message);
            if (head.Length == 0) return;
            activity.Record(level, TradingActivityLog.NotifiedSource, head,
                tail.Length == 0 ? delivery : $"{delivery} · {tail}");
        }
        catch { /* Recording must never cost a notification. */ }
    }

    /// <summary>The first non-empty line as the row, the others joined as one detail line; markdown markers dropped.</summary>
    internal static (string Head, string Tail) Split(string message)
    {
        var lines = (message ?? "").Split('\n')
            .Select(line => Plain(line))
            .Where(line => line.Length > 0)
            .ToList();
        return lines.Count == 0 ? ("", "") : (lines[0], string.Join(" · ", lines.Skip(1)));
    }

    private static string Plain(string line) =>
        new string(line.Trim().Where(c => c is not ('*' or '_' or '`' or '#') && c != '\r').ToArray()).Trim();

    /// <summary>
    /// Replaces the host's notifier registration with this wrapper around it. Called after the host has registered its own
    /// (plugins register after the host), and a no-op when there is none — a test container, a host without channels.
    /// </summary>
    internal static void Decorate(IServiceCollection services)
    {
        var existing = services.LastOrDefault(d => d.ServiceType == typeof(IUserNotifier) && !d.IsKeyedService);
        if (existing is null || services.Any(d => d.ServiceType == typeof(Decorated))) return;
        services.AddSingleton<Decorated>();
        services.Remove(existing);
        services.Add(ServiceDescriptor.Describe(typeof(IUserNotifier),
            sp => new ActivityRecordingNotifier(Inner(sp, existing), sp.GetRequiredService<TradingActivityLog>()),
            existing.Lifetime));
    }

    /// <summary>Marks a container already decorated, so a second call cannot wrap the wrapper.</summary>
    private sealed class Decorated;

    private static IUserNotifier Inner(IServiceProvider sp, ServiceDescriptor d) => (IUserNotifier)(
        d.ImplementationInstance
        ?? d.ImplementationFactory?.Invoke(sp)
        ?? ActivatorUtilities.CreateInstance(sp, d.ImplementationType!));
}
