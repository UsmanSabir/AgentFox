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
    /// <param name="executionUniverseOverride">
    /// Authoritative symbols resolved by the execution boundary. Required for Watchlist mode; when
    /// omitted in that mode validation fails closed. AllowedSymbols mode may use configuration.
    /// </param>
    /// <param name="liquidationUniverseOverride">
    /// Symbols with broker-confirmed positive custody. A SELL may reduce one of these positions even
    /// when the symbol is outside the entry universe; BUYs remain restricted to the selected universe.
    /// Null means no extra liquidation symbols are known.
    /// </param>
    RiskValidationResult Validate(
        IReadOnlyList<IReadOnlyList<TradingSignal>> groups,
        bool? killSwitchOverride = null,
        IReadOnlyCollection<string>? executionUniverseOverride = null,
        IReadOnlyCollection<string>? liquidationUniverseOverride = null);
}
