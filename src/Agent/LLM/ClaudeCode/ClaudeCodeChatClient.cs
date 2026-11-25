using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentFox.LLM.ClaudeCode;

public enum ClaudeCodeFailure
{
    NotSignedIn, NotInstalled, NotAllowed, ReauthenticationRequired, RateLimited, Busy, TimedOut, Failed,
}

/// <summary>A Claude Code call failed. The message is written for the owner and never holds a token.</summary>
public sealed class ClaudeCodeException(ClaudeCodeFailure kind, string message)
    : AiProviderException(message, retryable: kind is ClaudeCodeFailure.Busy)
{
    public ClaudeCodeFailure Kind { get; } = kind;
}

/// <summary>
/// An <see cref="IChatClient"/> over the official Claude Code CLI, signed in with the owner's own subscription.
/// TEXT AND JSON ONLY: the CLI runs its own tool loop and can never hand a tool call back, so tools in the request
/// are ignored, and the chat agent on this provider cannot read the account or trade.
///
/// Each call starts one CLI process (stateless, no session saved), one at a time by default. Nothing here retries:
/// a refused sign-in, an exhausted subscription or a call whose outcome is unknown is reported, never repeated.
/// </summary>
public sealed class ClaudeCodeChatClient : IChatClient
{
    internal const string JsonInstruction =
        "Reply with one JSON object only: no prose before or after it and no code fences.";

    private readonly ClaudeCodeOptions _options;
    private readonly ClaudeCodePaths _paths;
    private readonly ClaudeCodeStore _store;
    private readonly IClaudeCodeProcessRunner _runner;
    private readonly ClaudeCodeActivity _activity;
    private readonly ILogger<ClaudeCodeChatClient> _logger;
    private readonly SemaphoreSlim _gate;

    public ClaudeCodeChatClient(ClaudeCodeOptions options, ClaudeCodePaths paths, ClaudeCodeStore store,
        IClaudeCodeProcessRunner runner, ClaudeCodeActivity activity, ILogger<ClaudeCodeChatClient> logger)
    {
        _options = options;
        _paths = paths;
        _store = store;
        _runner = runner;
        _activity = activity;
        _logger = logger;
        _gate = new SemaphoreSlim(Math.Max(1, options.MaxConcurrentRequests));
    }

    /// <summary>The model the next call uses: the owner's choice, else the configured default.</summary>
    public string CurrentModel => _store.Read().Model ?? _options.DefaultModel;

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        await GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var token = _store.ReadToken()
                    ?? throw Fail(ClaudeCodeFailure.NotSignedIn, "Claude Code is not signed in. Sign in under Settings → Claude Code.");
        return StreamAsync(token, messages, options, cancellationToken);
    }

    /// <summary>One tiny call, with the saved token or with a candidate one before it is saved. Never throws for a
    /// failed call: the outcome is the answer.</summary>
    public async Task<(bool Passed, string Message)> TestAsync(string? candidateToken, CancellationToken cancellationToken)
    {
        var token = candidateToken ?? _store.ReadToken();
        if (token is null) return (false, "Claude Code is not signed in.");
        try
        {
            var reply = await StreamAsync(token,
                    [new ChatMessage(ChatRole.System, "You are a terse assistant."), new ChatMessage(ChatRole.User, "Reply with exactly: ok")],
                    null, cancellationToken)
                .ToChatResponseAsync(cancellationToken);
            var passed = reply.Text.Trim().Length > 0;
            _activity.RecordTest(passed);
            return (passed, passed ? "Claude Code answered." : "Claude Code answered with nothing.");
        }
        catch (ClaudeCodeException ex)
        {
            _activity.RecordTest(false);
            return (false, ex.Message);
        }
    }

    private async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(string token, IEnumerable<ChatMessage> messages,
        ChatOptions? options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var rendered = ClaudeCodePrompt.Render(messages);
        // options.ModelId is ignored on purpose: the agent framework fills it with the CONFIGURED provider's model,
        // which means nothing to the CLI.
        var model = CurrentModel;
        var (schema, wantsJson) = JsonRequest(options?.ResponseFormat);
        var systemPrompt = wantsJson && schema is null
            ? rendered.SystemPrompt + "\n\n" + JsonInstruction
            : rendered.SystemPrompt;

        await AcquireAsync(cancellationToken);
        var promptFile = Path.Combine(_paths.Work, $"system-{Guid.NewGuid():N}.txt");
        try
        {
            _paths.Ensure();
            _activity.SweepIfDue(_paths);
            await File.WriteAllTextAsync(promptFile, systemPrompt, new UTF8Encoding(false), cancellationToken);

            var invocation = new ClaudeCodeInvocation(
                _options.ExecutablePath,
                ClaudeCodeCommand.Arguments(model, promptFile, schema),
                ClaudeCodeCommand.Environment(Environment.GetEnvironmentVariables(), token, _paths),
                _paths.Work, rendered.UserMessage, TimeSpan.FromSeconds(Math.Max(10, _options.RequestTimeoutSeconds)));

            ClaudeCodeResult? result = null;
            ClaudeCodeProcessException? processFailure = null;
            string? modelId = null;
            var sawDelta = false;
            var sawText = false;
            var responseId = Guid.NewGuid().ToString("N");

            await using (var lines = _runner.RunAsync(invocation, cancellationToken).GetAsyncEnumerator(cancellationToken))
            {
                while (true)
                {
                    string line;
                    try
                    {
                        if (!await lines.MoveNextAsync()) break;
                        line = lines.Current;
                    }
                    catch (ClaudeCodeProcessException ex)
                    {
                        processFailure = ex;
                        break;
                    }

                    switch (ClaudeCodeStream.Parse(line))
                    {
                        case ClaudeCodeInit init:
                            modelId = init.Model;
                            break;
                        case ClaudeCodeTextDelta delta when !wantsJson:
                            sawDelta = sawText = true;
                            yield return Update(delta.Text, responseId, modelId);
                            break;
                        case ClaudeCodeAssistantText whole when !wantsJson && !sawDelta:
                            // Partial messages are on, so this only speaks when no delta did; otherwise it would
                            // repeat the whole reply.
                            sawText = true;
                            yield return Update(whole.Text, responseId, modelId);
                            break;
                        case ClaudeCodeUsageWindows usage:
                            _activity.RecordUsage(usage);
                            break;
                        case ClaudeCodeResult r:
                            result = r;
                            break;
                    }
                }
            }

            // A failed call still prints its result line and THEN exits non-zero; the result says why.
            if (result is null)
            {
                var failure = processFailure switch
                {
                    { NotInstalled: true } => Fail(ClaudeCodeFailure.NotInstalled, processFailure.Message),
                    { TimedOut: true } => Fail(ClaudeCodeFailure.TimedOut, processFailure.Message),
                    not null => Fail(ClaudeCodeFailure.Failed, processFailure.Message),
                    null => Fail(ClaudeCodeFailure.Failed, "Claude Code ended without an answer."),
                };
                if (processFailure?.StderrTail is { } tail)
                    _logger.LogWarning("[ClaudeCode] CLI failed: {Stderr}", tail);
                throw failure;
            }

            if (result.IsError)
                throw FailFromResult(result);

            if (!wantsJson && !sawText && !string.IsNullOrEmpty(result.Text))
                yield return Update(result.Text, responseId, modelId); // last resort: the result line's own copy

            if (wantsJson)
            {
                var json = result.StructuredOutputJson ?? StripCodeFence(result.Text ?? "");
                yield return Update(json, responseId, modelId);
            }

            var usageDetails = new UsageDetails { InputTokenCount = result.InputTokens, OutputTokenCount = result.OutputTokens };
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(usageDetails)])
            {
                ResponseId = responseId,
                ModelId = modelId,
                FinishReason = ChatFinishReason.Stop,
            };
            _activity.RecordSuccess(modelId ?? model);
        }
        finally
        {
            try { File.Delete(promptFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            _gate.Release();
        }
    }

    private async Task AcquireAsync(CancellationToken cancellationToken)
    {
        var wait = TimeSpan.FromSeconds(Math.Max(1, _options.QueueTimeoutSeconds));
        if (!await _gate.WaitAsync(wait, cancellationToken))
            throw Fail(ClaudeCodeFailure.Busy, "Claude Code is busy with another request. Try again in a moment.");
    }

    private ClaudeCodeException FailFromResult(ClaudeCodeResult r)
    {
        var text = ClaudeCodeTerminalText.Redact(r.Text ?? "");
        if (text.Length > 300) text = text[..300];
        if (r.ApiErrorStatus is 401 or 403
            || text.Contains("Not logged in", StringComparison.OrdinalIgnoreCase)
            || text.Contains("OAuth", StringComparison.OrdinalIgnoreCase) && text.Contains("invalid", StringComparison.OrdinalIgnoreCase))
            return Fail(ClaudeCodeFailure.ReauthenticationRequired,
                "Claude no longer accepts this sign-in. Sign in again under Settings → Claude Code.");
        if (r.ApiErrorStatus == 429 || text.Contains("usage limit", StringComparison.OrdinalIgnoreCase)
                                    || text.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
        {
            var resets = _activity.Usage?.ResetsAt;
            return Fail(ClaudeCodeFailure.RateLimited, resets is { } at
                ? $"Your Claude subscription's usage limit is reached. It resets at {at:yyyy-MM-dd HH:mm} UTC."
                : "Your Claude subscription's usage limit is reached. Try again later.");
        }
        return Fail(ClaudeCodeFailure.Failed, text.Length > 0 ? $"Claude Code could not answer: {text}" : "Claude Code could not answer.");
    }

    private ClaudeCodeException Fail(ClaudeCodeFailure kind, string message)
    {
        _activity.RecordFailure(kind, message);
        return new ClaudeCodeException(kind, message);
    }

    private static ChatResponseUpdate Update(string text, string responseId, string? modelId) =>
        new(ChatRole.Assistant, text) { ResponseId = responseId, ModelId = modelId };

    /// <summary>A real schema goes to the CLI; plain "JSON please" is asked for in words (see the class remarks).</summary>
    internal static (string? Schema, bool WantsJson) JsonRequest(ChatResponseFormat? format) => format switch
    {
        ChatResponseFormatJson { Schema: { } schema } => (schema.GetRawText(), true),
        ChatResponseFormatJson => (null, true),
        _ => (null, false),
    };

    internal static string StripCodeFence(string text)
    {
        var t = text.Trim();
        if (!t.StartsWith("```", StringComparison.Ordinal)) return t;
        var firstNewline = t.IndexOf('\n');
        var end = t.LastIndexOf("```", StringComparison.Ordinal);
        return firstNewline > 0 && end > firstNewline ? t[(firstNewline + 1)..end].Trim() : t;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is not null ? null
        : serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("claude-code", null, CurrentModel)
        : serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}
