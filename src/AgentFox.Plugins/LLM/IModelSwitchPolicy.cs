namespace AgentFox.LLM;

/// <summary>Controls which runtime model IDs may be selected. A null list allows any model.</summary>
public interface IModelSwitchPolicy
{
    Task<ModelSwitchSelection> ReadAsync(CancellationToken cancellationToken = default);
}

/// <param name="AllowedModels">Null permits any model; an empty list permits only the configured model.</param>
/// <param name="Reason">Shown when a model is refused, such as when policy could not be read.</param>
public sealed record ModelSwitchSelection(IReadOnlyList<string>? AllowedModels, string? Reason = null);

public sealed class AllowAnyModelSwitchPolicy : IModelSwitchPolicy
{
    public Task<ModelSwitchSelection> ReadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new ModelSwitchSelection(null));
}
