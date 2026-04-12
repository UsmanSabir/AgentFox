using AgentFox.Runtime;

namespace AgentFox.Agents;

/// <summary>
/// Singleton that exposes the <see cref="HeartbeatManager"/> and <see cref="CronScheduler"/>
/// once they have been created by <see cref="AgentOrchestrator"/>.
/// <para>
/// Follows the same holder pattern as <see cref="FoxAgentHolder"/>: other services
/// (e.g. WebModule scheduling endpoints) can inject this and check
/// <see cref="IsAvailable"/> before accessing the managers.
/// </para>
/// </summary>
public sealed class SchedulingHolder
{
    private volatile bool _published;
    private HeartbeatManager? _heartbeatManager;
    private CronScheduler? _cronScheduler;

    /// <summary>True after <see cref="Publish"/> has been called.</summary>
    public bool IsAvailable => _published;

    /// <summary>The heartbeat manager, or null before it is published.</summary>
    public HeartbeatManager? HeartbeatManager => _heartbeatManager;

    /// <summary>The cron scheduler, or null before it is published.</summary>
    public CronScheduler? CronScheduler => _cronScheduler;

    /// <summary>
    /// Called once by <see cref="AgentOrchestrator"/> after scheduling infrastructure is ready.
    /// Subsequent calls are no-ops.
    /// </summary>
    public void Publish(HeartbeatManager heartbeatManager, CronScheduler cronScheduler)
    {
        if (_published) return;
        _heartbeatManager = heartbeatManager;
        _cronScheduler    = cronScheduler;
        _published        = true;
    }
}
