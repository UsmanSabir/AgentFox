using System.Collections;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentFox.LLM;
using AgentFox.LLM.ClaudeCode;
using AgentFox.LLM.Subscription;
using AgentFox.Plugins;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFox.ChannelTests;

/// <summary>
/// The Claude Code provider without a real CLI or subscription. Event shapes are copied from CLI 2.1.258 output
/// captured on 2026-11-25 (ids and the token replaced).
/// </summary>
[TestClass]
public sealed class ClaudeCodeTests
{
    // Synthetic, and assembled at run time so no token-shaped literal sits in the source for a secret scanner to flag.
    private static readonly string Token = string.Join("-", "sk", "ant", "oat01", "AbCdEfGhIjKlMnOpQrStUvWxYz0123456789_")
        + "-AbCdEfGhIjKlMnOpQrStUvWxYz0123456789AbCdEfGhIjKlMnOp";

    // ── Recorded event shapes ─────────────────────────────────────────────────────────────────────

    private const string Init = """{"type":"system","subtype":"init","cwd":"/tmp/work","session_id":"s-1","tools":[],"mcp_servers":[],"model":"claude-haiku-4-5-20251001","permissionMode":"default","apiKeySource":"none","claude_code_version":"2.1.258","plugins":[]}""";
    private const string RateLimit = """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed_warning","resetsAt":1795629600,"rateLimitType":"seven_day","utilization":0.89,"isUsingOverage":false,"surpassedThreshold":0.75,"unifiedWindows":{"five_hour":{"utilization":0.1,"resetsAt":1795602000},"seven_day":{"utilization":0.89,"resetsAt":1795629600}}},"session_id":"s-1"}""";
    private const string ThinkingDelta = """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"secret reasoning"}},"session_id":"s-1","parent_tool_use_id":null}""";
    private const string ThinkingMessage = """{"type":"assistant","message":{"model":"claude-haiku-4-5-20251001","role":"assistant","content":[{"type":"thinking","thinking":"secret reasoning","signature":"x"}]},"parent_tool_use_id":null,"session_id":"s-1"}""";
    private static string Delta(string text) =>
        "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"text_delta\",\"text\":"
        + JsonSerializer.Serialize(text) + "}},\"session_id\":\"s-1\",\"parent_tool_use_id\":null}";
    private static string Assistant(string text) =>
        "{\"type\":\"assistant\",\"message\":{\"model\":\"claude-haiku-4-5-20251001\",\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":"
        + JsonSerializer.Serialize(text) + "}]},\"parent_tool_use_id\":null,\"session_id\":\"s-1\"}";
    private static string Result(string text, bool isError = false, int? apiError = null, string? structured = null) =>
        "{\"type\":\"result\",\"subtype\":\"" + (isError ? "error_during_execution" : "success") + "\",\"is_error\":" + (isError ? "true" : "false")
        + ",\"api_error_status\":" + (apiError?.ToString() ?? "null") + ",\"result\":" + JsonSerializer.Serialize(text)
        + ",\"session_id\":\"s-1\",\"total_cost_usd\":0.0018,\"usage\":{\"input_tokens\":676,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0,\"output_tokens\":38}"
        + (structured is null ? "" : ",\"structured_output\":" + structured) + "}";

    // ── Fakes ─────────────────────────────────────────────────────────────────────────────────────

    private sealed class FakeRunner(Func<CliInvocation, CancellationToken, IAsyncEnumerable<string>> run) : ICliProcessRunner
    {
        public List<CliInvocation> Calls { get; } = [];
        public IAsyncEnumerable<string> RunAsync(CliInvocation invocation, CancellationToken cancellationToken)
        {
            lock (Calls) Calls.Add(invocation);
            return run(invocation, cancellationToken);
        }

        public static FakeRunner Lines(params string[] lines) => new((_, _) => Yield(lines, null));
        public static FakeRunner LinesThenFail(CliProcessException ex, params string[] lines) => new((_, _) => Yield(lines, ex));

        private static async IAsyncEnumerable<string> Yield(string[] lines, Exception? after)
        {
            foreach (var l in lines) { await Task.Yield(); yield return l; }
            if (after is not null) throw after;
        }
    }

    private sealed class Harness : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "cc-tests-" + Guid.NewGuid().ToString("N"));
        public ClaudeCodeOptions Options { get; } = new() { QueueTimeoutSeconds = 1 };
        public ClaudeCodePaths Paths { get; }
        public ClaudeCodeStore Store { get; }
        public ClaudeCodeActivity Activity { get; } = new();

        public Harness(bool signedIn = true)
        {
            Paths = new ClaudeCodePaths(Path.Combine(Dir, "claude-code"));
            var protector = new AesPluginSecretProtector(Path.Combine(Dir, ".key"), NullLogger<AesPluginSecretProtector>.Instance);
            Store = new ClaudeCodeStore(Path.Combine(Dir, "claude-code.json"), protector, NullLogger<ClaudeCodeStore>.Instance);
            if (signedIn) Store.SaveToken(Token, "paste");
        }

        public ClaudeCodeChatClient Client(ICliProcessRunner runner) =>
            new(Options, Paths, Store, runner, Activity, NullLogger<ClaudeCodeChatClient>.Instance);

        public AiRoutingStore Routing => _routing ??= new AiRoutingStore(Path.Combine(Dir, "ai-routing.json"),
            Store.LegacyUseFor, NullLogger<AiRoutingStore>.Instance);
        private AiRoutingStore? _routing;

        public AiClientRouter Router(RuntimeModelChatClient configured, IServiceProvider services, params ISubscriptionProvider[] providers) =>
            new(configured, providers, Routing, services);

        public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
    }

    private static readonly ChatMessage[] Hello = [new(ChatRole.System, "Be brief."), new(ChatRole.User, "Say ok")];

    // ── Stream parsing ────────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Parses_the_measured_events_and_never_surfaces_thinking()
    {
        var init = (ClaudeCodeInit)ClaudeCodeStream.Parse(Init)!;
        Assert.AreEqual("claude-haiku-4-5-20251001", init.Model);
        Assert.AreEqual("2.1.258", init.Version);

        var usage = (ClaudeCodeUsageWindows)ClaudeCodeStream.Parse(RateLimit)!;
        Assert.AreEqual(0.1, usage.FiveHourUtilization);
        Assert.AreEqual(0.89, usage.SevenDayUtilization);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1795629600), usage.SevenDayResetsAt);

        Assert.IsNull(ClaudeCodeStream.Parse(ThinkingDelta));
        Assert.IsNull(ClaudeCodeStream.Parse(ThinkingMessage));
        Assert.AreEqual("ok", ((ClaudeCodeTextDelta)ClaudeCodeStream.Parse(Delta("ok"))!).Text);

        var result = (ClaudeCodeResult)ClaudeCodeStream.Parse(Result("ok"))!;
        Assert.IsFalse(result.IsError);
        Assert.AreEqual("ok", result.Text);
        Assert.AreEqual(676, result.InputTokens);
        Assert.AreEqual(38, result.OutputTokens);
    }

    [TestMethod]
    public void Malformed_unknown_and_future_lines_are_skipped()
    {
        Assert.IsNull(ClaudeCodeStream.Parse(""));
        Assert.IsNull(ClaudeCodeStream.Parse("not json"));
        Assert.IsNull(ClaudeCodeStream.Parse("{\"type\":\"result\""));
        Assert.IsNull(ClaudeCodeStream.Parse("""{"type":"something_new","x":1}"""));
        Assert.IsNull(ClaudeCodeStream.Parse("""{"type":"system","subtype":"hook_started"}"""));
        Assert.IsNull(ClaudeCodeStream.Parse("[1,2]"));
    }

    // ── What setup-token prints ───────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Reads_a_wrapped_token_and_link_through_terminal_codes()
    {
        var head = Token[..80];
        var tail = Token[80..];
        var raw = "\x1b[?25l✓Long-lived authentication token created successfully!\r\n" +
                  " Your OAuth token (valid for 1 year):\r\n\x1b[1m " + head + "\x1b(B\r\n " + tail + "\x1b[0m\r\n" +
                  "Store this token securely. You won't be able to see it again.\r\n";
        Assert.AreEqual(Token, ClaudeCodeTerminalText.FindToken(ClaudeCodeTerminalText.Strip(raw)));

        var linkRaw = "Browser didn't open? Use the url below to sign in (c to copy)\r\n" +
                      "https://claude.com/cai/oauth/authorize?code=true&client_id=9d1c&response_type=code&redi\r\n" +
                      "rect_uri=https%3A%2F%2Fplatform.claude.com%2Foauth%2Fcode%2Fcallback&scope=user%3Ainference&state=abc\r\n" +
                      "\x1b[2mPaste code here if prompted > ";
        var stripped = ClaudeCodeTerminalText.Strip(linkRaw);
        Assert.IsTrue(ClaudeCodeTerminalText.IsAwaitingCode(stripped));
        Assert.AreEqual("https://claude.com/cai/oauth/authorize?code=true&client_id=9d1c&response_type=code&redirect_uri=https%3A%2F%2Fplatform.claude.com%2Foauth%2Fcode%2Fcallback&scope=user%3Ainference&state=abc",
            ClaudeCodeTerminalText.FindSignInLink(stripped));

        Assert.AreEqual("Socket is closed", ClaudeCodeTerminalText.FindError("OAuth error: Socket is closed\n Press Enter to retry."));
        Assert.IsNull(ClaudeCodeTerminalText.FindToken("valid for 1 year): sk-ant-oat01-short Store"));
        Assert.AreEqual("key sk-ant-**** here", ClaudeCodeTerminalText.Redact("key " + Token + " here"));
    }

    [TestMethod]
    public void Reads_the_link_and_token_as_CLI_2_1_258_prints_them_with_the_agents_config()
    {
        // The shape captured on a hosted agent, 2026-11-25: words placed with cursor moves, the link carried as an OSC 8
        // hyperlink AND printed, then the copy hint the CLI shows with the agent's own config folder.
        const string state = "NWFd6h5Ybe5DWLQBJOap-Qn1RpFJlr80Qb3mg8bV5Lo";
        var link = "https://claude.com/cai/oauth/authorize?code=true&client_id=9d1c250a-e61b-44d9-88ed-5944d1962f5e&response_type=code" +
                   "&redirect_uri=https%3A%2F%2Fplatform.claude.com%2Foauth%2Fcode%2Fcallback&scope=user%3Ainference" +
                   "&code_challenge=1r6k64mULkmZkcjGZu8zOWme5RPHCViFb8gxOgaWHDE&code_challenge_method=S256&state=" + state;
        var prompt =
            "\x1b[2G\x1b[37mBrowser\x1b[10Gdidn't\x1b[17Gopen?\x1b[23GUse\x1b[27Gthe\x1b[31Gurl\x1b[35Gbelow\x1b[39m\r\r\n\r\r\n" +
            "\x1b]8;id=1hwv8zt;" + link + "\x07\x1b[37m" + link + "\x1b[39m\x1b]8;;\x07\r\r\n\r\r\n" +
            "\x1b[2G\x1b[37mHold\x1b[7GShift\x1b[13Gwhile\x1b[19Gselecting\x1b[29Gto\x1b[32Guse\x1b[36Gyour\x1b[41Gterminal's\x1b[52Gnative\x1b[59Gcopy\x1b[39m\r\r\n\r\r\n\r\r\n" +
            "\x1b[2GPaste\x1b[8Gcode\x1b[13Ghere\x1b[18Gif\x1b[21Gprompted\x1b[30G>\r\r\n\x1b[>0q\x1b[c";

        Assert.IsTrue(ClaudeCodeTerminalText.IsAwaitingCode(ClaudeCodeTerminalText.Strip(prompt)));
        Assert.AreEqual(link, ClaudeCodeTerminalText.FindSignInLinkInRaw(prompt));
        // Without the hyperlink the printed text still yields the link, not the hint glued onto its state.
        var printedOnly = prompt.Replace("\x1b]8;id=1hwv8zt;" + link + "\x07", "");
        Assert.AreEqual(link, ClaudeCodeTerminalText.FindSignInLinkInRaw(printedOnly));
        Assert.IsNull(ClaudeCodeTerminalText.FindSignInLink(link + "Holdterminal'snativecopy"));

        var done = prompt + "\x1b(B\x0f\r\x1b[31C\x1b[1A****8bV5Lo\r\r\n\r\x1b[1C\x1b[8A\x1b[K\r\x1b[1C\x1b[1B\x1b[32m✓ Long-lived authentication token created successfully!" +
                   "\r\x1b[1B\x1b[39m\x1b[K\r\x1b[1C\x1b[1BYour\x1b[7GOAuth\x1b[13Gtoken\x1b[19G(valid\x1b[26Gfor\x1b[30G1\x1b[32Gyear):" +
                   "\r\x1b[1C\x1b[1B\x1b[K\r\x1b[1C\x1b[1B\x1b[93m" + Token + "\r\x1b[1C\x1b[2B\x1b[37mStore this token securely. You won't be able to see it again.\x1b[39m\x1b[K\r\r\n";
        Assert.AreEqual(Token, ClaudeCodeTerminalText.FindToken(ClaudeCodeTerminalText.Strip(done)));
    }

    // ── How the CLI is started ────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void The_child_environment_is_an_allow_list_that_cannot_reach_paid_billing()
    {
        var parent = new Hashtable
        {
            ["PATH"] = "/usr/bin", ["ANTHROPIC_API_KEY"] = "sk-ant-api-x", ["ANTHROPIC_AUTH_TOKEN"] = "y",
            ["ANTHROPIC_BASE_URL"] = "https://evil.example", ["CLAUDE_CONFIG_DIR"] = "/home/owner/.claude",
            ["OPENAI_API_KEY"] = "z", ["LLM__ApiKey"] = "w", ["HTTPS_PROXY"] = "http://proxy:8080", ["HOME"] = "/home/owner",
        };
        var paths = new ClaudeCodePaths("/data/claude-code");
        var env = ClaudeCodeCommand.Environment(parent, Token, paths);

        foreach (var banned in new[] { "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL", "OPENAI_API_KEY", "LLM__ApiKey" })
            Assert.IsFalse(env.ContainsKey(banned), banned);
        Assert.AreEqual(Token, env["CLAUDE_CODE_OAUTH_TOKEN"]);
        Assert.AreEqual(paths.Config, env["CLAUDE_CONFIG_DIR"]);
        Assert.AreEqual(paths.Home, env["HOME"]);
        Assert.AreEqual("/usr/bin", env["PATH"]);
        Assert.AreEqual("http://proxy:8080", env["HTTPS_PROXY"]);
        Assert.AreEqual("1", env["DISABLE_AUTOUPDATER"]);
        Assert.AreEqual("1", env["CLAUDE_CODE_DISABLE_BUNDLED_SKILLS"]);

        Assert.IsFalse(ClaudeCodeCommand.Environment(parent, null, paths).ContainsKey("CLAUDE_CODE_OAUTH_TOKEN"));
    }

    [TestMethod]
    public void Arguments_disable_every_tool_and_never_carry_the_prompt()
    {
        var args = ClaudeCodeCommand.Arguments("sonnet", "/data/sp.txt", null);
        var i = args.IndexOf("--tools");
        Assert.AreEqual("", args[i + 1]);
        CollectionAssert.Contains(args, "--strict-mcp-config");
        CollectionAssert.Contains(args, "--no-session-persistence");
        CollectionAssert.Contains(args, "--include-partial-messages");
        Assert.AreEqual("/data/sp.txt", args[args.IndexOf("--system-prompt-file") + 1]);
        Assert.IsFalse(args.Any(a => a.Contains("dangerously") || a == "--bare" || a == "--json-schema"));
        Assert.AreEqual("{\"type\":\"object\"}", ClaudeCodeCommand.Arguments("sonnet", "f", "{\"type\":\"object\"}")[^1]);
    }

    [TestMethod]
    public void Renders_one_message_alone_and_a_conversation_as_a_transcript()
    {
        var single = SubscriptionPrompt.Render([new ChatMessage(ChatRole.User, "سلام، کیا حال ہے؟")]);
        Assert.AreEqual(SubscriptionPrompt.DefaultSystemPrompt, single.SystemPrompt);
        Assert.AreEqual("سلام، کیا حال ہے؟", single.UserMessage);

        var many = SubscriptionPrompt.Render([
            new ChatMessage(ChatRole.System, "A"), new ChatMessage(ChatRole.System, "B"),
            new ChatMessage(ChatRole.User, "first"), new ChatMessage(ChatRole.Assistant, "reply"),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", "tool output")]),
            new ChatMessage(ChatRole.User, [new TextContent("second"), new DataContent(new byte[] { 1 }, "image/png")]),
        ]);
        Assert.AreEqual("A\n\nB", many.SystemPrompt);
        StringAssert.Contains(many.UserMessage, "<user>\nfirst\n</user>");
        StringAssert.Contains(many.UserMessage, "<assistant>\nreply\n</assistant>");
        StringAssert.Contains(many.UserMessage, "cannot read attachments");
        Assert.IsFalse(many.UserMessage.Contains("tool output"));
        Assert.IsTrue(many.UserMessage.EndsWith("second\n[An attachment was included here, but this model cannot read attachments.]"));
    }

    // ── The chat client ───────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Streams_deltas_once_and_does_not_repeat_the_whole_message()
    {
        using var h = new Harness();
        var runner = FakeRunner.Lines(Init, RateLimit, ThinkingDelta, ThinkingMessage, Delta("Hel"), Delta("lo"),
            Assistant("Hello"), Result("Hello"));
        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in h.Client(runner).GetStreamingResponseAsync(Hello)) updates.Add(u);

        Assert.AreEqual("Hello", string.Concat(updates.Select(u => u.Text)));
        var last = updates[^1];
        Assert.AreEqual(ChatFinishReason.Stop, last.FinishReason);
        var usage = last.Contents.OfType<UsageContent>().Single().Details;
        Assert.AreEqual(676, usage.InputTokenCount);
        Assert.AreEqual(38, usage.OutputTokenCount);
        Assert.AreEqual("claude-haiku-4-5-20251001", h.Activity.LastModel);
        Assert.AreEqual(89, (int)Math.Round(h.Activity.Usage!.SevenDayUtilization!.Value * 100));

        var call = runner.Calls.Single();
        Assert.AreEqual("Say ok", call.StandardInput);
        Assert.AreEqual(Token, call.Environment["CLAUDE_CODE_OAUTH_TOKEN"]);
        Assert.AreEqual("sonnet", call.Arguments[call.Arguments.ToList().IndexOf("--model") + 1]);
        Assert.IsFalse(Directory.EnumerateFiles(h.Paths.Work).Any(), "the system prompt file is removed after the call");
    }

    [TestMethod]
    public async Task Uses_the_whole_message_when_no_delta_arrived()
    {
        using var h = new Harness();
        var reply = await h.Client(FakeRunner.Lines(Init, Assistant("ok"), Result("ok"))).GetResponseAsync(Hello);
        Assert.AreEqual("ok", reply.Text);
    }

    [TestMethod]
    public async Task Plain_json_is_asked_for_in_words_and_its_fence_removed()
    {
        using var h = new Harness();
        string? systemPrompt = null;
        var runner = new FakeRunner((inv, _) =>
        {
            systemPrompt = File.ReadAllText(inv.Arguments[inv.Arguments.ToList().IndexOf("--system-prompt-file") + 1]);
            return Lines(Init, Delta("```json\n"), Delta("{\"a\":1}\n```"), Result("```json\n{\"a\":1}\n```"));
        });
        var reply = await h.Client(runner).GetResponseAsync(Hello, new ChatOptions { ResponseFormat = ChatResponseFormat.Json });
        Assert.AreEqual("{\"a\":1}", reply.Text);
        StringAssert.EndsWith(systemPrompt, SubscriptionPrompt.JsonInstruction);
        Assert.IsFalse(runner.Calls.Single().Arguments.Contains("--json-schema"));
    }

    [TestMethod]
    public async Task A_real_schema_goes_to_the_cli_and_structured_output_is_returned()
    {
        using var h = new Harness();
        var runner = FakeRunner.Lines(Init, Result("done", structured: "{\"verdict\":\"HOLD\"}"));
        var schema = JsonDocument.Parse("""{"type":"object","properties":{"verdict":{"type":"string"}}}""").RootElement;
        var reply = await h.Client(runner).GetResponseAsync(Hello,
            new ChatOptions { ResponseFormat = ChatResponseFormat.ForJsonSchema(schema) });
        Assert.AreEqual("{\"verdict\":\"HOLD\"}", reply.Text);
        Assert.IsTrue(runner.Calls.Single().Arguments.Contains("--json-schema"));
    }

    [TestMethod]
    public async Task A_refused_sign_in_asks_to_sign_in_again_and_the_exit_code_after_it_is_ignored()
    {
        using var h = new Harness();
        var runner = FakeRunner.LinesThenFail(new CliProcessException("exit 1", 1),
            Init, Result("Failed to authenticate. API Error: 401 OAuth access token is invalid.", isError: true, apiError: 401));
        var ex = await Assert.ThrowsExactlyAsync<SubscriptionCliException>(() => h.Client(runner).GetResponseAsync(Hello));
        Assert.AreEqual(SubscriptionFailure.ReauthenticationRequired, ex.Kind);
        Assert.AreEqual(SubscriptionFailure.ReauthenticationRequired, h.Activity.LastErrorKind);
    }

    [TestMethod]
    public async Task A_usage_limit_names_its_reset_time_and_is_not_retried()
    {
        using var h = new Harness();
        var runner = FakeRunner.Lines(Init, RateLimit, Result("Claude usage limit reached", isError: true, apiError: 429));
        var ex = await Assert.ThrowsExactlyAsync<SubscriptionCliException>(() => h.Client(runner).GetResponseAsync(Hello));
        Assert.AreEqual(SubscriptionFailure.RateLimited, ex.Kind);
        StringAssert.Contains(ex.Message, "resets at");
        Assert.AreEqual(1, runner.Calls.Count);
    }

    [TestMethod]
    public async Task Not_signed_in_and_not_installed_are_told_apart()
    {
        using (var h = new Harness(signedIn: false))
        {
            var ex = await Assert.ThrowsExactlyAsync<SubscriptionCliException>(() => h.Client(FakeRunner.Lines()).GetResponseAsync(Hello));
            Assert.AreEqual(SubscriptionFailure.NotSignedIn, ex.Kind);
        }
        using (var h = new Harness())
        {
            var runner = FakeRunner.LinesThenFail(new CliProcessException("not found") { NotInstalled = true });
            var ex = await Assert.ThrowsExactlyAsync<SubscriptionCliException>(() => h.Client(runner).GetResponseAsync(Hello));
            Assert.AreEqual(SubscriptionFailure.NotInstalled, ex.Kind);
        }
    }

    [TestMethod]
    public async Task An_unexpected_end_without_a_result_is_a_failure_not_an_empty_answer()
    {
        using var h = new Harness();
        var runner = FakeRunner.LinesThenFail(new CliProcessException("exit 137", 137, "Killed"), Init, Delta("par"));
        var ex = await Assert.ThrowsExactlyAsync<SubscriptionCliException>(() => h.Client(runner).GetResponseAsync(Hello));
        Assert.AreEqual(SubscriptionFailure.Failed, ex.Kind);
    }

    [TestMethod]
    public async Task Cancellation_stops_the_call_and_frees_the_slot()
    {
        using var h = new Harness();
        var calls = 0;
        var runner = new FakeRunner((_, ct) => Interlocked.Increment(ref calls) == 1 ? Hang(ct) : Lines(Init, Result("ok")));
        var client = h.Client(runner);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAsync<OperationCanceledException>(() => client.GetResponseAsync(Hello, cancellationToken: cts.Token));
        Assert.AreEqual("ok", (await client.GetResponseAsync(Hello)).Text);
    }

    [TestMethod]
    public async Task A_second_call_waits_its_turn_then_reports_busy()
    {
        using var h = new Harness();
        using var release = new CancellationTokenSource();
        var runner = new FakeRunner((_, ct) => Hang(CancellationTokenSource.CreateLinkedTokenSource(ct, release.Token).Token));
        var client = h.Client(runner);
        var first = client.GetResponseAsync(Hello);
        await Task.Delay(100);
        var ex = await Assert.ThrowsExactlyAsync<SubscriptionCliException>(() => client.GetResponseAsync(Hello));
        Assert.AreEqual(SubscriptionFailure.Busy, ex.Kind);
        release.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => first);
    }

    // ── The store ─────────────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void The_token_is_encrypted_at_rest_and_read_back_only_for_the_cli()
    {
        using var h = new Harness();
        var onDisk = File.ReadAllText(Path.Combine(h.Dir, "claude-code.json"));
        Assert.IsFalse(onDisk.Contains("sk-ant-"), "plaintext token on disk");
        StringAssert.Contains(onDisk, "enc:v1:");
        Assert.AreEqual(Token, h.Store.ReadToken());
        var settings = h.Store.Read();
        Assert.IsTrue(settings.HasToken);
        Assert.AreEqual("paste", settings.TokenSource);
        Assert.IsFalse(JsonSerializer.Serialize(settings).Contains("sk-ant-"));

        Assert.ThrowsExactly<ArgumentException>(() => h.Store.SaveToken("sk-ant-api03-not-a-setup-token-xxxxxxxxxxxxxxxx", "paste"));
        h.Store.RemoveToken();
        Assert.IsNull(h.Store.ReadToken());
        Assert.IsFalse(h.Store.Read().HasToken);
    }

    [TestMethod]
    public void Routing_defaults_send_trading_tasks_to_claude_and_keep_chat_and_choices_persist()
    {
        using var h = new Harness();
        Assert.AreEqual(AiProviders.Configured, h.Routing.ProviderFor(AiPurposes.Chat));
        Assert.AreEqual(AiProviders.ClaudeCode, h.Routing.ProviderFor(AiPurposes.StockAssessment));
        Assert.AreEqual(AiProviders.ClaudeCode, h.Routing.ProviderFor(AiPurposes.RebuyAdvice));

        h.Routing.Set(AiPurposes.Chat, AiProviders.Codex);
        h.Routing.Set(AiPurposes.StockAssessment, AiProviders.Configured);
        var reread = new AiRoutingStore(Path.Combine(h.Dir, "ai-routing.json"), _ => null, NullLogger<AiRoutingStore>.Instance);
        Assert.AreEqual(AiProviders.Codex, reread.ProviderFor(AiPurposes.Chat));
        Assert.AreEqual(AiProviders.Configured, reread.ProviderFor(AiPurposes.StockAssessment));
        Assert.AreEqual(AiProviders.ClaudeCode, reread.ProviderFor(AiPurposes.RebuyAdvice));
        Assert.ThrowsExactly<ArgumentException>(() => h.Routing.Set("something-else", AiProviders.Codex));
        Assert.ThrowsExactly<ArgumentException>(() => h.Routing.Set(AiPurposes.Chat, "gemini"));

        h.Store.SaveModel("opus");
        var protector = new AesPluginSecretProtector(Path.Combine(h.Dir, ".key"), NullLogger<AesPluginSecretProtector>.Instance);
        var store = new ClaudeCodeStore(Path.Combine(h.Dir, "claude-code.json"), protector, NullLogger<ClaudeCodeStore>.Instance).Read();
        Assert.AreEqual("opus", store.Model);
        Assert.IsTrue(store.HasToken);
    }

    [TestMethod]
    public void Switches_saved_before_routing_existed_carry_over()
    {
        using var h = new Harness();
        // The Claude-only version kept per-purpose booleans in claude-code.json; write that shape directly.
        var path = Path.Combine(h.Dir, "claude-code.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json["useForChat"] = true;
        json["useForStockAssessment"] = false;
        File.WriteAllText(path, json.ToJsonString());
        var protector = new AesPluginSecretProtector(Path.Combine(h.Dir, ".key"), NullLogger<AesPluginSecretProtector>.Instance);
        var old = new ClaudeCodeStore(path, protector, NullLogger<ClaudeCodeStore>.Instance);
        var routing = new AiRoutingStore(Path.Combine(h.Dir, "routing2.json"), old.LegacyUseFor, NullLogger<AiRoutingStore>.Instance);
        Assert.AreEqual(AiProviders.ClaudeCode, routing.ProviderFor(AiPurposes.Chat));
        Assert.AreEqual(AiProviders.Configured, routing.ProviderFor(AiPurposes.StockAssessment));
        Assert.AreEqual(AiProviders.ClaudeCode, routing.ProviderFor(AiPurposes.RebuyAdvice), "never saved: the default");
    }

    [TestMethod]
    public void An_unreadable_file_or_key_is_reported_not_treated_as_signed_in()
    {
        using var h = new Harness();
        var other = new AesPluginSecretProtector(Path.Combine(h.Dir, ".other-key"), NullLogger<AesPluginSecretProtector>.Instance);
        var wrongKey = new ClaudeCodeStore(Path.Combine(h.Dir, "claude-code.json"), other, NullLogger<ClaudeCodeStore>.Instance);
        Assert.IsFalse(wrongKey.Read().HasToken);
        Assert.IsNotNull(wrongKey.LoadProblem);

        var corrupt = Path.Combine(h.Dir, "corrupt.json");
        File.WriteAllText(corrupt, "{ not json");
        var store = new ClaudeCodeStore(corrupt, other, NullLogger<ClaudeCodeStore>.Instance);
        Assert.IsFalse(store.Read().HasToken);
        Assert.IsNotNull(store.LoadProblem);
    }

    // ── Routing ───────────────────────────────────────────────────────────────────────────────────

    private sealed class Policy(Func<SubscriptionAccess> read) : ISubscriptionAccessPolicy
    {
        public Task<SubscriptionAccess> ReadAsync(string provider, CancellationToken cancellationToken = default) => Task.FromResult(read());
    }

    [TestMethod]
    public async Task Each_purpose_goes_to_its_provider_only_when_signed_in_chosen_and_allowed()
    {
        using var h = new Harness();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["LLM:Provider"] = "ollama", ["LLM:Model"] = "local" }).Build();
        using var configured = new RuntimeModelChatClient(config);
        var claude = h.Client(FakeRunner.Lines(Init, Result("ok")));
        var access = new SubscriptionAccess(true);
        Func<SubscriptionAccess> read = () => access;
        using var services = new ServiceCollection().AddSingleton<ISubscriptionAccessPolicy>(new Policy(() => read())).BuildServiceProvider();
        var router = h.Router(configured, services, claude);

        Assert.IsFalse(await router.UsesSubscriptionAsync(AiPurposes.Chat, default), "chat is off by default");
        Assert.IsTrue(await router.UsesSubscriptionAsync(AiPurposes.RebuyAdvice, default));

        access = new SubscriptionAccess(false, "switched off");
        Assert.IsFalse(await router.UsesSubscriptionAsync(AiPurposes.RebuyAdvice, default));

        read = () => throw new InvalidOperationException("hub down");
        Assert.IsFalse(await router.UsesSubscriptionAsync(AiPurposes.RebuyAdvice, default), "unreadable policy keeps the configured provider");

        read = () => new SubscriptionAccess(true);
        h.Store.RemoveToken();
        Assert.IsFalse(await router.UsesSubscriptionAsync(AiPurposes.RebuyAdvice, default));

        Assert.AreSame(router.For(AiPurposes.Chat), router.For(AiPurposes.Chat));
        Assert.AreSame(configured, router.For(AiPurposes.Chat).GetService(typeof(RuntimeModelChatClient)));
    }

    [TestMethod]
    public async Task A_routed_purpose_actually_reaches_claude()
    {
        using var h = new Harness();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["LLM:Provider"] = "ollama", ["LLM:Model"] = "local" }).Build();
        using var configured = new RuntimeModelChatClient(config);
        var runner = FakeRunner.Lines(Init, Delta("from claude"), Result("from claude"));
        using var services = new ServiceCollection().BuildServiceProvider();
        var router = h.Router(configured, services, h.Client(runner));
        var reply = await router.For(AiPurposes.StockAssessment).GetResponseAsync(Hello);
        Assert.AreEqual("from claude", reply.Text);
        Assert.AreEqual(1, runner.Calls.Count);
    }

    // ── Status and retention ──────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Status_reads_version_and_estimated_expiry_and_never_carries_the_token()
    {
        using var h = new Harness();
        var runner = new FakeRunner((inv, _) => inv.Arguments[0] == "--version"
            ? Lines("2.1.258 (Claude Code)")
            : Fail(new CliProcessException("exit 1", 1), """{"loggedIn":true,""", """ "authMethod":"oauth_token"}"""));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["LLM:Provider"] = "ollama" }).Build();
        using var configured = new RuntimeModelChatClient(config);
        using var services = new ServiceCollection().BuildServiceProvider();
        var router = h.Router(configured, services, h.Client(runner));
        var service = new ClaudeCodeStatusService(h.Options, h.Paths, h.Store, h.Activity, router, runner);

        var status = await service.GetAsync(refresh: true, default);
        Assert.AreEqual("ready", status.State);
        Assert.AreEqual("2.1.258", status.Version);
        Assert.AreEqual("oauth_token", status.AuthMethod);
        Assert.IsTrue(status.SignedIn);
        Assert.AreEqual(365, (int)Math.Round((status.TokenExpiresEstimateUtc!.Value - status.TokenSavedUtc!.Value).TotalDays));
        CollectionAssert.Contains(status.Models.ToList(), "sonnet");
        var json = JsonSerializer.Serialize(status);
        Assert.IsFalse(json.Contains("sk-ant-"), json);
        Assert.IsFalse(runner.Calls.Any(c => c.StandardInput is { Length: > 0 }), "status never makes a model call");
    }

    [TestMethod]
    public async Task Status_says_not_installed_when_the_cli_is_missing()
    {
        using var h = new Harness();
        var runner = FakeRunner.LinesThenFail(new CliProcessException("not found") { NotInstalled = true });
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["LLM:Provider"] = "ollama" }).Build();
        using var configured = new RuntimeModelChatClient(config);
        using var services = new ServiceCollection().BuildServiceProvider();
        var router = h.Router(configured, services, h.Client(runner));
        var status = await new ClaudeCodeStatusService(h.Options, h.Paths, h.Store, h.Activity, router, runner).GetAsync(true, default);
        Assert.AreEqual("not-installed", status.State);
        Assert.IsFalse(status.Installed);
    }

    [TestMethod]
    public void Housekeeping_removes_old_cli_leftovers_and_keeps_its_config()
    {
        using var h = new Harness();
        h.Paths.Ensure();
        var backups = Directory.CreateDirectory(Path.Combine(h.Paths.Config, "backups")).FullName;
        var oldBackup = Path.Combine(backups, ".claude.json.backup.1");
        var newBackup = Path.Combine(backups, ".claude.json.backup.2");
        var config = Path.Combine(h.Paths.Config, ".claude.json");
        var oldProject = Path.Combine(Directory.CreateDirectory(Path.Combine(h.Paths.Config, "projects", "-tmp-work")).FullName, "x.jsonl");
        foreach (var f in new[] { oldBackup, newBackup, config, oldProject }) File.WriteAllText(f, "{}");
        foreach (var f in new[] { oldBackup, config, oldProject }) File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-3));

        Assert.AreEqual(2, SubscriptionHousekeeping.Sweep(h.Paths.SweptDirectories, DateTimeOffset.UtcNow));
        Assert.IsFalse(File.Exists(oldBackup));
        Assert.IsFalse(File.Exists(oldProject));
        Assert.IsFalse(Directory.Exists(Path.Combine(h.Paths.Config, "projects", "-tmp-work")));
        Assert.IsTrue(File.Exists(newBackup));
        Assert.IsTrue(File.Exists(config), "the CLI's own config is never swept");
    }

    // ── The real process runner, with an ordinary shell standing in for the CLI ────────────────────

    [TestMethod]
    public async Task The_runner_reads_lines_reports_the_exit_code_and_a_missing_executable()
    {
        var paths = new ClaudeCodePaths(Path.Combine(Path.GetTempPath(), "cc-run-" + Guid.NewGuid().ToString("N")));
        paths.Ensure();
        try
        {
            var env = ClaudeCodeCommand.Environment(Environment.GetEnvironmentVariables(), null, paths);
            var (exe, ok, fail) = OperatingSystem.IsWindows()
                ? ("cmd.exe", new[] { "/c", "echo one& echo two" }, new[] { "/c", "echo partial& exit 3" })
                : ("/bin/sh", new[] { "-c", "echo one; echo two" }, new[] { "-c", "echo partial; exit 3" });
            var runner = new CliProcessRunner();

            var lines = new List<string>();
            await foreach (var l in runner.RunAsync(new CliInvocation(exe, ok, env, paths.Work, "", TimeSpan.FromSeconds(20)), default))
                lines.Add(l.Trim());
            CollectionAssert.AreEqual(new[] { "one", "two" }, lines);

            var seen = new List<string>();
            var ex = await Assert.ThrowsExactlyAsync<CliProcessException>(async () =>
            {
                await foreach (var l in runner.RunAsync(new CliInvocation(exe, fail, env, paths.Work, "", TimeSpan.FromSeconds(20)), default))
                    seen.Add(l.Trim());
            });
            Assert.AreEqual(3, ex.ExitCode);
            CollectionAssert.AreEqual(new[] { "partial" }, seen);

            var missing = await Assert.ThrowsExactlyAsync<CliProcessException>(async () =>
            {
                await foreach (var _ in runner.RunAsync(new CliInvocation("definitely-not-claude-" + Guid.NewGuid().ToString("N"),
                                   [], env, paths.Work, "", TimeSpan.FromSeconds(5)), default)) { }
            });
            Assert.IsTrue(missing.NotInstalled);
        }
        finally
        {
            try { Directory.Delete(paths.Root, true); } catch { }
        }
    }

    [TestMethod]
    public async Task The_runner_kills_a_process_that_outlives_its_timeout()
    {
        var paths = new ClaudeCodePaths(Path.Combine(Path.GetTempPath(), "cc-run-" + Guid.NewGuid().ToString("N")));
        paths.Ensure();
        try
        {
            var env = ClaudeCodeCommand.Environment(Environment.GetEnvironmentVariables(), null, paths);
            var (exe, args) = OperatingSystem.IsWindows()
                ? ("cmd.exe", new[] { "/c", "ping -n 30 127.0.0.1 >nul" })
                : ("/bin/sh", new[] { "-c", "sleep 30" });
            var started = DateTime.UtcNow;
            var ex = await Assert.ThrowsExactlyAsync<CliProcessException>(async () =>
            {
                await foreach (var _ in new CliProcessRunner().RunAsync(
                                   new CliInvocation(exe, args, env, paths.Work, "", TimeSpan.FromSeconds(1)), default)) { }
            });
            Assert.IsTrue(ex.TimedOut, $"{ex.Message} exit={ex.ExitCode} stderr={ex.StderrTail}");
            Assert.IsTrue(DateTime.UtcNow - started < TimeSpan.FromSeconds(15));
        }
        finally
        {
            try { Directory.Delete(paths.Root, true); } catch { }
        }
    }

    /// <summary>Manual only: set CLAUDE_CODE_IT_TOKEN (a real setup token) to make one tiny real call. Costs a
    /// sliver of the subscription; never runs in CI.</summary>
    [TestMethod, TestCategory("Manual")]
    public async Task Real_cli_answers_with_a_real_token()
    {
        var token = Environment.GetEnvironmentVariable("CLAUDE_CODE_IT_TOKEN");
        if (string.IsNullOrWhiteSpace(token)) { Assert.Inconclusive("Set CLAUDE_CODE_IT_TOKEN to run."); return; }
        using var h = new Harness(signedIn: false);
        h.Store.SaveToken(token.Trim(), "paste");
        h.Store.SaveModel("haiku");
        var (passed, message) = await h.Client(new CliProcessRunner()).TestAsync(null, default);
        Assert.IsTrue(passed, message);
    }

    /// <summary>Manual only: set CLAUDE_CODE_IT=1 on a machine with Claude Code installed. Reads the real version
    /// and sign-in state through the real runner; makes no model call and needs no token.</summary>
    [TestMethod, TestCategory("Manual")]
    public async Task Real_cli_status_reads_the_version_without_a_token()
    {
        if (Environment.GetEnvironmentVariable("CLAUDE_CODE_IT") != "1") { Assert.Inconclusive("Set CLAUDE_CODE_IT=1 to run."); return; }
        using var h = new Harness(signedIn: false);
        var runner = new CliProcessRunner();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["LLM:Provider"] = "ollama" }).Build();
        using var configured = new RuntimeModelChatClient(config);
        using var services = new ServiceCollection().BuildServiceProvider();
        var router = h.Router(configured, services, h.Client(runner));
        var status = await new ClaudeCodeStatusService(h.Options, h.Paths, h.Store, h.Activity, router, runner).GetAsync(true, default);
        Console.WriteLine($"state={status.State} version={status.Version} signInSupported={status.SignInSupported}");
        Assert.IsTrue(status.Installed);
        Assert.AreEqual("not-signed-in", status.State);
        Assert.IsTrue(Directory.Exists(h.Paths.Config));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────

    private static async IAsyncEnumerable<string> Lines(params string[] lines)
    {
        foreach (var l in lines) { await Task.Yield(); yield return l; }
    }

    private static async IAsyncEnumerable<string> Fail(Exception after, params string[] lines)
    {
        foreach (var l in lines) { await Task.Yield(); yield return l; }
        throw after;
    }

    private static async IAsyncEnumerable<string> Hang([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        yield break;
    }
}
