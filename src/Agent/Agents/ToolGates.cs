using Microsoft.Extensions.Logging;

namespace AgentFox.Agents;

/// <summary>
/// Evaluates a specialist's optional per-tool availability predicates
/// (<see cref="AgentFox.Plugins.Interfaces.SpecialistAgentDescriptor.ToolGates"/>).
///
/// <para>
/// The one rule that matters is the failure direction: a predicate that throws makes ITS tool unavailable for that call and
/// nothing else. Offering a tool because its gate could not be read is how a gate silently becomes decoration. A cancelled
/// caller is the exception, because that is the turn ending rather than the gate failing.
/// </para>
/// </summary>
internal static class ToolGates
{
    /// <summary>True when the tool has no gate or its gate says yes; false when the gate says no or throws.</summary>
    internal static async ValueTask<bool> IsAvailableAsync(
        IReadOnlyDictionary<string, Func<CancellationToken, ValueTask<bool>>>? gates,
        string toolName,
        CancellationToken ct,
        ILogger? logger)
    {
        if (gates is not { Count: > 0 }) return true;
        Func<CancellationToken, ValueTask<bool>>? gate = null;
        foreach (var (name, candidate) in gates)
            if (name.Equals(toolName, StringComparison.OrdinalIgnoreCase)) { gate = candidate; break; }
        if (gate is null) return true;

        try
        {
            return await gate(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Availability check for tool '{ToolName}' failed; it is treated as unavailable for this call.", toolName);
            return false;
        }
    }
}
