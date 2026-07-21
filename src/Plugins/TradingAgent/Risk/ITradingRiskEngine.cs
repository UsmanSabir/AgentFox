using TradingAgent.Models;

namespace TradingAgent.Risk;

public sealed record RiskValidationResult(bool Allowed, IReadOnlyList<string> Violations)
{
    public static RiskValidationResult Allow() => new(true, []);
    public static RiskValidationResult Reject(params string[] violations) => new(false, violations);
}

public interface ITradingRiskEngine
{
    /// <param name="killSwitchOverride">
    /// The current runtime kill-switch value (from <see cref="TradingAgent.Config.TradingPolicyProvider"/>).
    /// Omit to fall back to the static appsettings value.
    /// </param>
    RiskValidationResult Validate(IReadOnlyList<IReadOnlyList<TradingSignal>> groups, bool? killSwitchOverride = null);
}
