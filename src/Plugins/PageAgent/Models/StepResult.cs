namespace PageAgent.Models;

/// <summary>Records what happened during a single step of the autonomous loop.</summary>
public sealed class StepResult
{
    public int Step { get; init; }
    public AgentAction Action { get; init; } = new();
    public string Output { get; init; } = string.Empty;
    public bool Success { get; init; }
    public string? Error { get; init; }
    public TimeSpan Elapsed { get; init; }

    public override string ToString()
    {
        var status = Success ? "OK" : $"FAIL({Error})";
        return $"[Step {Step:D2}] {Action} → {status} ({Elapsed.TotalSeconds:F1}s)";
    }
}
