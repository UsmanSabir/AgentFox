using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingAgent.Config;

namespace TradingAgent.Safety;

/// <summary>Prevents an unsafe live-execution configuration from starting.</summary>
public sealed class TradingSafetyStartupValidator : IHostedService
{
    private static readonly string[] ExecutionTools = ["place_order", "place_orders"];
    private readonly TradingPolicyProvider _policyProvider;
    private readonly IOptions<AhkConfig> _ahk;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TradingSafetyStartupValidator> _logger;

    public TradingSafetyStartupValidator(
        TradingPolicyProvider policyProvider,
        IOptions<AhkConfig> ahk,
        IConfiguration configuration,
        ILogger<TradingSafetyStartupValidator> logger)
    {
        _policyProvider = policyProvider;
        _ahk = ahk;
        _configuration = configuration;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var policy = _policyProvider.Current();
        var mode = policy.ExecutionMode.Trim().ToUpperInvariant();
        if (!policy.AutoExecute || mode is "DISABLED" or "PAPER" or "SHADOW")
            return Task.CompletedTask;

        if (mode is not ("APPROVALREQUIRED" or "BOUNDEDAUTO"))
            throw new InvalidOperationException(
                $"Unknown TradingAgent.ExecutionMode '{policy.ExecutionMode}'. Live execution is blocked.");

        if (_ahk.Value.AllowMarketOrders)
            _logger.LogWarning(
                "[TradingSafety] Market orders are enabled and cannot be pre-trade value capped reliably.");

        if (mode == "APPROVALREQUIRED")
        {
            var enabled = _configuration.GetValue<bool>("Hitl:Enabled");
            var watched = _configuration.GetSection("Hitl:RequireApprovalForTools").Get<string[]>() ?? [];
            var missing = ExecutionTools.Except(watched, StringComparer.OrdinalIgnoreCase).ToArray();
            if (!enabled || missing.Length > 0)
                throw new InvalidOperationException(
                    "ApprovalRequired trading cannot start unless Hitl.Enabled=true and both " +
                    "place_order and place_orders are listed in Hitl.RequireApprovalForTools.");
        }

        _logger.LogWarning(
            "[TradingSafety] LIVE execution enabled. Mode={Mode}, PolicyVersion={PolicyVersion}.",
            policy.ExecutionMode, policy.Version);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
