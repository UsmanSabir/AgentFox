using TradingAgent.Models;

namespace TradingAgent.Risk;

public sealed record RiskValidationResult(bool Allowed, IReadOnlyList<string> Violations)
{
    public static RiskValidationResult Allow() => new(true, []);
    public static RiskValidationResult Reject(params string[] violations) => new(false, violations);
}

public interface ITradingRiskEngine
{
    RiskValidationResult Validate(IReadOnlyList<IReadOnlyList<TradingSignal>> groups);
}
