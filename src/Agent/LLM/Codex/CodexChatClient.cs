using System.Runtime.CompilerServices;
using System.Text;
using AgentFox.LLM.Subscription;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentFox.LLM.Codex;

/// <summary>
/// An <see cref="IChatClient"/> over the official Codex CLI, signed in with the owner's own ChatGPT subscription.
/// TEXT AND JSON ONLY: every tool feature is disabled (<see cref="CodexCommand.DisabledFeatures"/>), so tools in the
/// request are ignored and a chat on this provider cannot read the account or trade.
///
/// Codex has no system-prompt option and keeps its own coding-agent instructions (a one-word answer costs ~14.5k input
/// tokens, measured 2026-11-25), so the caller's instructions travel inside the message. Codex streams no text: the
/// answer arrives whole, and only the turn's LAST agent message is the answer. Nothing here retries.
/// </summary>
public sealed class CodexChatClient : IChatClient, ISubscriptionProvider
{
    private readonly CodexOptions _options;
    private readonly CodexPaths _paths;
    private readonly CodexStore _store;
    private readonly ICliProcessRunner _runner;
    private readonly CodexActivity _activity;
    private readonly ILogger<CodexChatClient> _logger;
    private readonly SemaphoreSlim _gate;

    public CodexChatClient(CodexOptions options, CodexPaths paths, CodexStore store, ICliProcessRunner runner,
        CodexActivity activity, ILogger<CodexChatClient> logger)
    {
        _options = options;
        _paths = paths;
        _store = store;
        _runner = runner;
        _activity = activity;
        _logger = logger;
        _gate = new SemaphoreSlim(Math.Max(1, options.MaxConcurrentRequests));
    }

    public string Id => AiProviders.Codex;
    public string DisplayName => "Codex";
    public bool IsSignedIn => File.Exists(_paths.AuthFile);
    public IChatClient Client => this;

    /// <summary>The model the next call uses; null means the CLI's default for the account.</summary>
    public string? CurrentModel => _store.Read().Model ?? (string.IsNullOrWhiteSpace(_options.DefaultModel) ? null : _options.DefaultModel);

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        await GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

    /// <summary>One tiny call. Never throws for a failed call: the outcome is the answer.</summary>
    public async Task<(bool Passed, string Message)> TestAsync(CancellationToken cancellationToken)
    {
        try
        {
            var reply = await GetResponseAsync([new ChatMessage(ChatRole.User, "Reply with exactly: ok")], null, cancellationToken);
            var passed = reply.Text.Trim().Length > 0;
            _activity.RecordTest(passed);
            return (passed, passed ? "Codex answered." : "Codex answered with nothing.");
        }
        catch (SubscriptionCliException ex)
        {
            _activity.RecordTest(false);
            return (false, ex.Message);
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!IsSignedIn)
            throw Fail(SubscriptionFailure.NotSignedIn, "Codex is not signed in. Sign in under Settings → Codex.");
        var rendered = SubscriptionPrompt.Render(messages);
        var (schema, wantsJson) = SubscriptionPrompt.JsonRequest(options?.ResponseFormat);
        var settings = _store.Read();
        var model = CurrentModel;

        await AcquireAsync(cancellationToken);
        var schemaFile = schema is null ? null : Path.Combine(_paths.Work, $"schema-{Guid.NewGuid():N}.json");
        try
        {
            _paths.Ensure();
            _activity.SweepIfDue(_paths.SweptDirectories);
            if (schemaFile is not null)
                await File.WriteAllTextAsync(schemaFile, schema, new UTF8Encoding(false), cancellationToken);

            var invocation = new CliInvocation(
                _options.ExecutablePath,
                CodexCommand.Arguments(model, settings.ReasoningEffort, schemaFile),
                CodexCommand.Environment(Environment.GetEnvironmentVariables(), _paths),
                _paths.Work, Compose(rendered, wantsJson && schema is null),
                TimeSpan.FromSeconds(Math.Max(10, _options.RequestTimeoutSeconds)));

            string? answer = null;
            CodexTurnCompleted? completed = null;
            CodexFailed? failed = null;
            CliProcessException? processFailure = null;

            await using (var lines = _runner.RunAsync(invocation, cancellationToken).GetAsyncEnumerator(cancellationToken))
            {
                while (true)
                {
                    try
                    {
                        if (!await lines.MoveNextAsync()) break;
                    }
                    catch (CliProcessException ex)
                    {
                        processFailure = ex;
                        break;
                    }
                    switch (CodexStream.Parse(lines.Current))
                    {
                        case CodexAgentMessage m: answer = m.Text; break;   // the last one wins
                        case CodexTurnCompleted c: completed = c; break;
                        case CodexFailed f: failed = f; break;
                        case CodexToolAttempt t:
                            _logger.LogWarning("[Codex] The model reached for a tool ({Type}) although every tool feature is disabled.", t.ItemType);
                            break;
                    }
                }
            }

            if (failed is not null && answer is null)
                throw FailFrom(failed.Message);
            if (answer is null)
            {
                if (processFailure?.StderrTail is { } tail) _logger.LogWarning("[Codex] CLI failed: {Stderr}", tail);
                throw processFailure switch
                {
                    { NotInstalled: true } => Fail(SubscriptionFailure.NotInstalled, "The Codex CLI is not installed where this agent runs."),
                    { TimedOut: true } => Fail(SubscriptionFailure.TimedOut, "The Codex call took too long and was stopped."),
                    { StderrTail: { } t } => FailFrom(t),
                    _ => Fail(SubscriptionFailure.Failed, "Codex ended without an answer."),
                };
            }

            var responseId = Guid.NewGuid().ToString("N");
            var text = wantsJson ? SubscriptionPrompt.StripCodeFence(answer) : answer;
            yield return new ChatResponseUpdate(ChatRole.Assistant, text) { ResponseId = responseId, ModelId = model };
            var usage = new UsageDetails { InputTokenCount = completed?.InputTokens, OutputTokenCount = completed?.OutputTokens };
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(usage)])
            {
                ResponseId = responseId,
                ModelId = model,
                FinishReason = ChatFinishReason.Stop,
            };
            _activity.RecordSuccess(model ?? "default");
        }
        finally
        {
            if (schemaFile is not null)
                try { File.Delete(schemaFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            _gate.Release();
        }
    }

    /// <summary>The caller's instructions ride in the message, marked off from the conversation.</summary>
    internal static string Compose(SubscriptionPrompt.Rendered rendered, bool askForJson)
    {
        var sb = new StringBuilder();
        if (rendered.SystemPrompt != SubscriptionPrompt.DefaultSystemPrompt || askForJson)
        {
            sb.Append("<instructions>\n").Append(rendered.SystemPrompt);
            if (askForJson) sb.Append("\n\n").Append(SubscriptionPrompt.JsonInstruction);
            sb.Append("\n</instructions>\n\n");
        }
        sb.Append(rendered.UserMessage);
        return sb.ToString();
    }

    private async Task AcquireAsync(CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(Math.Max(1, _options.QueueTimeoutSeconds)), cancellationToken))
            throw Fail(SubscriptionFailure.Busy, "Codex is busy with another request. Try again in a moment.");
    }

    /// <summary>Classifies Codex's own error text. The exact wordings are not yet measured, so the matching is broad and
    /// the default is a plain failure carrying the (redacted) text.</summary>
    internal SubscriptionCliException FailFrom(string message)
    {
        var text = SecretRedaction.Redact(message).Trim();
        if (text.Length > 300) text = text[..300];
        if (Has(text, "401") || Has(text, "unauthorized") || Has(text, "not logged in") || Has(text, "log in again")
            || Has(text, "login") && Has(text, "expired") || Has(text, "refresh token"))
            return Fail(SubscriptionFailure.ReauthenticationRequired, "ChatGPT no longer accepts this sign-in. Sign in again under Settings → Codex.");
        if (Has(text, "usage limit") || Has(text, "rate limit") || Has(text, "429") || Has(text, "quota"))
            return Fail(SubscriptionFailure.RateLimited, "Your ChatGPT plan's usage limit is reached. Try again later.");
        if (Has(text, "model") && (Has(text, "not supported") || Has(text, "does not exist") || Has(text, "not found")))
            return Fail(SubscriptionFailure.Failed, $"Codex refused the model: {text}");
        return Fail(SubscriptionFailure.Failed, text.Length > 0 ? $"Codex could not answer: {text}" : "Codex could not answer.");
    }

    private static bool Has(string text, string part) => text.Contains(part, StringComparison.OrdinalIgnoreCase);

    private SubscriptionCliException Fail(SubscriptionFailure kind, string message)
    {
        _activity.RecordFailure(kind, message);
        return new SubscriptionCliException(kind, message);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is not null ? null
        : serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("codex", null, CurrentModel ?? "default")
        : serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}
