using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentFox.LLM.ClaudeCode;

public sealed record ClaudeCodeUsageView(
    string? Status, int? FiveHourPercent, DateTimeOffset? FiveHourResetsUtc,
    int? SevenDayPercent, DateTimeOffset? SevenDayResetsUtc, DateTimeOffset ReadUtc);

/// <summary>Everything the settings card shows. Holds no token.</summary>
public sealed record ClaudeCodeStatus(
    string State, bool Installed, string? Version, bool Allowed, string? NotAllowedReason,
    bool SignedIn, string? TokenSource, DateTimeOffset? TokenSavedUtc, DateTimeOffset? TokenExpiresEstimateUtc,
    bool ExpiringSoon, string Model, IReadOnlyList<string> Models,
    bool UseForChat, bool UseForStockAssessment, bool UseForRebuyAdvice, bool SignInSupported,
    DateTimeOffset? LastAuthCheckUtc, string? AuthMethod,
    DateTimeOffset? LastSuccessUtc, string? LastModel, DateTimeOffset? LastErrorUtc, string? LastError,
    DateTimeOffset? LastTestUtc, bool? LastTestPassed, ClaudeCodeUsageView? Usage, string? Problem);

/// <summary>
/// Reads the CLI's version and sign-in state. Neither costs subscription usage: <c>--version</c> and
/// <c>auth status</c> make no model call, and "signed in" from them is NOT proof a model call will work — that is
/// what Test does, and only when the owner presses it.
/// </summary>
public sealed partial class ClaudeCodeStatusService
{
    private static readonly TimeSpan VersionCacheFor = TimeSpan.FromMinutes(10);

    private readonly ClaudeCodeOptions _options;
    private readonly ClaudeCodePaths _paths;
    private readonly ClaudeCodeStore _store;
    private readonly ClaudeCodeActivity _activity;
    private readonly AiClientRouter _router;
    private readonly IClaudeCodeProcessRunner _runner;
    private (bool Installed, string? Version, DateTimeOffset CheckedUtc)? _version;
    private (DateTimeOffset CheckedUtc, string? AuthMethod)? _auth;

    public ClaudeCodeStatusService(ClaudeCodeOptions options, ClaudeCodePaths paths, ClaudeCodeStore store,
        ClaudeCodeActivity activity, AiClientRouter router, IClaudeCodeProcessRunner runner)
    {
        _options = options;
        _paths = paths;
        _store = store;
        _activity = activity;
        _router = router;
        _runner = runner;
    }

    public async Task<ClaudeCodeStatus> GetAsync(bool refresh, CancellationToken cancellationToken)
    {
        var (installed, version) = await VersionAsync(refresh, cancellationToken);
        var settings = _store.Read();
        if (refresh && installed && settings.HasToken) await CheckAuthAsync(cancellationToken);
        var access = await _router.ReadAccessAsync(cancellationToken);

        var expires = settings.TokenSavedUtc?.AddDays(_options.TokenLifetimeDays);
        var now = DateTimeOffset.UtcNow;
        var expiringSoon = expires is { } e && e > now && e - now <= TimeSpan.FromDays(_options.TokenExpiryWarningDays);
        var rateLimited = _activity.LastErrorKind == ClaudeCodeFailure.RateLimited
                          && _activity.LastErrorUtc > _activity.LastSuccessUtc.GetValueOrDefault();
        var state = !installed ? "not-installed"
            : !access.Allowed ? "not-allowed"
            : !settings.HasToken ? "not-signed-in"
            : _activity.LastErrorKind == ClaudeCodeFailure.ReauthenticationRequired ? "reauthentication-required"
            : expires is { } x && x <= now ? "expired"
            : rateLimited ? "rate-limited"
            : expiringSoon ? "expiring-soon"
            : "ready";

        var models = _options.Models.Where(m => !string.IsNullOrWhiteSpace(m)).ToList();
        var model = settings.Model ?? _options.DefaultModel;
        if (!models.Contains(model, StringComparer.OrdinalIgnoreCase)) models.Add(model);

        return new ClaudeCodeStatus(
            state, installed, version, access.Allowed, access.Reason,
            settings.HasToken, settings.TokenSource, settings.TokenSavedUtc, expires, expiringSoon, model, models,
            settings.UseForChat, settings.UseForStockAssessment, settings.UseForRebuyAdvice, ClaudeCodeSignIn.IsSupported,
            _auth?.CheckedUtc, _auth?.AuthMethod,
            _activity.LastSuccessUtc, _activity.LastModel, _activity.LastErrorUtc, _activity.LastError,
            _activity.LastTestUtc, _activity.LastTestPassed, UsageView(), _store.LoadProblem);
    }

    private ClaudeCodeUsageView? UsageView()
    {
        var u = _activity.Usage;
        if (u is null || _activity.UsageReadUtc is not { } read) return null;
        return new ClaudeCodeUsageView(u.Status, Percent(u.FiveHourUtilization), u.FiveHourResetsAt,
            Percent(u.SevenDayUtilization), u.SevenDayResetsAt, read);
        static int? Percent(double? v) => v is { } d ? (int)Math.Round(d * 100) : null;
    }

    private async Task<(bool Installed, string? Version)> VersionAsync(bool refresh, CancellationToken cancellationToken)
    {
        if (!refresh && _version is { } cached && DateTimeOffset.UtcNow - cached.CheckedUtc < VersionCacheFor)
            return (cached.Installed, cached.Version);
        var (lines, failure) = await RunAsync(["--version"], token: null, cancellationToken);
        var match = lines.Select(l => VersionPattern().Match(l)).FirstOrDefault(m => m.Success);
        var installed = failure is not { NotInstalled: true } && match is not null;
        _version = (installed, match?.Groups[1].Value, DateTimeOffset.UtcNow);
        return (installed, match?.Groups[1].Value);
    }

    private async Task CheckAuthAsync(CancellationToken cancellationToken)
    {
        var token = _store.ReadToken();
        if (token is null) return;
        var (lines, _) = await RunAsync(["auth", "status"], token, cancellationToken); // exits 1 when signed out
        string? method = null;
        try
        {
            using var doc = JsonDocument.Parse(string.Join('\n', lines));
            if (doc.RootElement.TryGetProperty("authMethod", out var m) && m.ValueKind == JsonValueKind.String)
                method = m.GetString();
        }
        catch (JsonException) { }
        _auth = (DateTimeOffset.UtcNow, method);
    }

    private async Task<(List<string> Lines, ClaudeCodeProcessException? Failure)> RunAsync(
        string[] args, string? token, CancellationToken cancellationToken)
    {
        _paths.Ensure();
        var invocation = new ClaudeCodeInvocation(_options.ExecutablePath, args,
            ClaudeCodeCommand.Environment(Environment.GetEnvironmentVariables(), token, _paths),
            _paths.Work, StandardInput: null, Timeout: TimeSpan.FromSeconds(20));
        var lines = new List<string>();
        try
        {
            await foreach (var line in _runner.RunAsync(invocation, cancellationToken))
                if (lines.Count < 200) lines.Add(line);
            return (lines, null);
        }
        catch (ClaudeCodeProcessException ex)
        {
            return (lines, ex);
        }
    }

    [GeneratedRegex(@"^\s*(\d+\.\d+\.\d+)")] private static partial Regex VersionPattern();
}
