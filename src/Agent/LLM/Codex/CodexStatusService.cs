using System.Text.RegularExpressions;
using AgentFox.LLM.Subscription;

namespace AgentFox.LLM.Codex;

/// <summary>Everything the Codex card shows. Holds no credential.</summary>
public sealed record CodexStatus(
    string State, bool Installed, string? Version, bool Allowed, string? NotAllowedReason,
    bool SignedIn, string? SignedInWith, string Model, IReadOnlyList<string> Models, string? ReasoningEffort,
    DateTimeOffset? LastAuthCheckUtc,
    DateTimeOffset? LastSuccessUtc, string? LastModel, DateTimeOffset? LastErrorUtc, string? LastError,
    DateTimeOffset? LastTestUtc, bool? LastTestPassed);

/// <summary>
/// Reads the Codex CLI's version and sign-in state (<c>--version</c>, <c>login status</c>): neither makes a model call
/// or costs plan usage, and "signed in" from them is NOT proof a call will work — that is what Test does.
/// </summary>
public sealed partial class CodexStatusService
{
    public const string DefaultModelLabel = "default";
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

    private readonly CodexOptions _options;
    private readonly CodexPaths _paths;
    private readonly CodexStore _store;
    private readonly CodexActivity _activity;
    private readonly AiClientRouter _router;
    private readonly ICliProcessRunner _runner;
    private (bool Installed, string? Version, DateTimeOffset At)? _version;
    private (string? With, DateTimeOffset At)? _auth;

    public CodexStatusService(CodexOptions options, CodexPaths paths, CodexStore store, CodexActivity activity,
        AiClientRouter router, ICliProcessRunner runner)
    {
        _options = options;
        _paths = paths;
        _store = store;
        _activity = activity;
        _router = router;
        _runner = runner;
    }

    public async Task<CodexStatus> GetAsync(bool refresh, CancellationToken cancellationToken)
    {
        if (refresh || _version is not { } v || DateTimeOffset.UtcNow - v.At > CacheFor)
        {
            var (lines, failure) = await RunAsync(["--version"], cancellationToken);
            var m = lines.Select(l => VersionPattern().Match(l)).FirstOrDefault(x => x.Success);
            _version = (failure is not { NotInstalled: true } && m is not null, m?.Groups[1].Value, DateTimeOffset.UtcNow);
        }
        var signedIn = File.Exists(_paths.AuthFile);
        if (_version.Value.Installed && signedIn && (refresh || _auth is null))
        {
            var (lines, _) = await RunAsync(["login", "status"], cancellationToken);
            var line = lines.FirstOrDefault(l => l.StartsWith("Logged in using ", StringComparison.OrdinalIgnoreCase));
            _auth = (line?["Logged in using ".Length..].Trim(), DateTimeOffset.UtcNow);
            if (lines.Any(l => l.Contains("Not logged in", StringComparison.OrdinalIgnoreCase))) signedIn = false;
        }

        var access = await _router.ReadAccessAsync(AiProviders.Codex, cancellationToken);
        var settings = _store.Read();
        var rateLimited = _activity.LastErrorKind == SubscriptionFailure.RateLimited
                          && _activity.LastErrorUtc > _activity.LastSuccessUtc.GetValueOrDefault();
        var state = !_version.Value.Installed ? "not-installed"
            : !access.Allowed ? "not-allowed"
            : !signedIn ? "not-signed-in"
            : _activity.LastErrorKind == SubscriptionFailure.ReauthenticationRequired ? "reauthentication-required"
            : rateLimited ? "rate-limited"
            : "ready";

        var model = settings.Model ?? (string.IsNullOrWhiteSpace(_options.DefaultModel) ? DefaultModelLabel : _options.DefaultModel);
        var models = new List<string> { DefaultModelLabel };
        models.AddRange(_options.Models.Where(x => !string.IsNullOrWhiteSpace(x) && x != DefaultModelLabel));
        if (!models.Contains(model, StringComparer.OrdinalIgnoreCase)) models.Add(model);

        return new CodexStatus(state, _version.Value.Installed, _version.Value.Version, access.Allowed, access.Reason,
            signedIn, _auth?.With, model, models, settings.ReasoningEffort, _auth?.At,
            _activity.LastSuccessUtc, _activity.LastModel, _activity.LastErrorUtc, _activity.LastError,
            _activity.LastTestUtc, _activity.LastTestPassed);
    }

    /// <summary><c>codex logout</c>: the CLI removes its own sign-in.</summary>
    public async Task SignOutAsync(CancellationToken cancellationToken)
    {
        await RunAsync(["logout"], cancellationToken);
        _auth = null;
        _activity.ClearErrors();
    }

    private async Task<(List<string> Lines, CliProcessException? Failure)> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        _paths.Ensure();
        var invocation = new CliInvocation(_options.ExecutablePath, args,
            CodexCommand.Environment(Environment.GetEnvironmentVariables(), _paths), _paths.Work, null, TimeSpan.FromSeconds(20));
        var lines = new List<string>();
        try
        {
            await foreach (var line in _runner.RunAsync(invocation, cancellationToken))
                if (lines.Count < 200) lines.Add(line);
            return (lines, null);
        }
        catch (CliProcessException ex)
        {
            return (lines, ex);
        }
    }

    // "codex-cli 0.172.0"
    [GeneratedRegex(@"(\d+\.\d+\.\d+)")] private static partial Regex VersionPattern();
}
