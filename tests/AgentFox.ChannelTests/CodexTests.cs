using System.Collections;
using System.Text.Json;
using AgentFox.LLM;
using AgentFox.LLM.Codex;
using AgentFox.LLM.Subscription;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFox.ChannelTests;

/// <summary>
/// The Codex provider without a real CLI or plan. Event shapes are copied from Codex CLI 0.172.0 output captured on
/// 2026-11-25 (thread ids replaced).
/// </summary>
[TestClass]
public sealed class CodexTests
{
    private const string ThreadStarted = """{"type":"thread.started","thread_id":"t-1"}""";
    private const string TurnStarted = """{"type":"turn.started"}""";
    private const string TurnCompleted = """{"type":"turn.completed","usage":{"input_tokens":14637,"cached_input_tokens":12928,"cache_write_input_tokens":0,"output_tokens":5,"reasoning_output_tokens":0}}""";
    private const string CommandStarted = """{"type":"item.started","item":{"id":"item_1","type":"command_execution","command":"/bin/sh -lc 'cat /tmp/work/secret.txt'","aggregated_output":"","exit_code":null,"status":"in_progress"}}""";
    private const string CommandCompleted = """{"type":"item.completed","item":{"id":"item_1","type":"command_execution","command":"/bin/sh -lc 'cat /tmp/work/secret.txt'","aggregated_output":"canary","exit_code":0,"status":"completed"}}""";
    private static string Agent(string text, string id = "item_0") =>
        "{\"type\":\"item.completed\",\"item\":{\"id\":\"" + id + "\",\"type\":\"agent_message\",\"text\":" + JsonSerializer.Serialize(text) + "}}";

    private sealed class FakeRunner(Func<CliInvocation, IAsyncEnumerable<string>> run) : ICliProcessRunner
    {
        public List<CliInvocation> Calls { get; } = [];
        public IAsyncEnumerable<string> RunAsync(CliInvocation invocation, CancellationToken cancellationToken)
        {
            Calls.Add(invocation);
            return run(invocation);
        }
        public static FakeRunner Lines(params string[] lines) => new(_ => Yield(lines, null));
        public static FakeRunner LinesThenFail(CliProcessException ex, params string[] lines) => new(_ => Yield(lines, ex));
        public static async IAsyncEnumerable<string> Yield(string[] lines, Exception? after)
        {
            foreach (var l in lines) { await Task.Yield(); yield return l; }
            if (after is not null) throw after;
        }
    }

    private sealed class Harness : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "codex-tests-" + Guid.NewGuid().ToString("N"));
        public CodexOptions Options { get; } = new() { QueueTimeoutSeconds = 1 };
        public CodexPaths Paths { get; }
        public CodexStore Store { get; }
        public CodexActivity Activity { get; } = new();

        public Harness(bool signedIn = true)
        {
            Paths = new CodexPaths(Path.Combine(Dir, "codex"));
            Paths.Ensure();
            if (signedIn) File.WriteAllText(Paths.AuthFile, "{}"); // the CLI's own file; only its presence matters here
            Store = new CodexStore(Path.Combine(Dir, "codex.json"), NullLogger<CodexStore>.Instance);
        }

        public CodexChatClient Client(ICliProcessRunner runner) =>
            new(Options, Paths, Store, runner, Activity, NullLogger<CodexChatClient>.Instance);

        public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
    }

    private static readonly ChatMessage[] Hello = [new(ChatRole.System, "Be brief."), new(ChatRole.User, "Say ok")];

    // ── How the CLI is started ────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Arguments_switch_every_tool_off_keep_the_read_only_sandbox_and_read_stdin()
    {
        var args = CodexCommand.Arguments("gpt-x", "high", "/data/schema.json");
        foreach (var flag in new[] { "exec", "--json", "--ephemeral", "--ignore-user-config", "--ignore-rules", "--skip-git-repo-check" })
            CollectionAssert.Contains(args, flag);
        Assert.AreEqual("read-only", args[args.IndexOf("--sandbox") + 1]);
        CollectionAssert.Contains(args, "approval_policy=\"never\"");
        var disabled = args.Select((a, i) => (a, i)).Where(x => x.a == "--disable").Select(x => args[x.i + 1]).ToList();
        CollectionAssert.IsSubsetOf(new[] { "shell_tool", "unified_exec", "plugins", "hooks", "apps", "browser_use", "computer_use" }, disabled);
        Assert.AreEqual("gpt-x", args[args.IndexOf("-m") + 1]);
        CollectionAssert.Contains(args, "model_reasoning_effort=\"high\"");
        Assert.AreEqual("/data/schema.json", args[args.IndexOf("--output-schema") + 1]);
        Assert.AreEqual("-", args[^1], "the prompt is read from stdin");
        Assert.IsFalse(args.Any(a => a.Contains("dangerously") || a == "--full-auto" || a == "--search" || a == "danger-full-access"));

        var bare = CodexCommand.Arguments(null, null, null);
        Assert.IsFalse(bare.Contains("-m") || bare.Contains("--output-schema") || bare.Any(a => a.StartsWith("model_reasoning_effort")));
    }

    [TestMethod]
    public void The_child_environment_cannot_reach_api_billing_and_uses_a_private_codex_home()
    {
        var parent = new Hashtable
        {
            ["PATH"] = "/usr/bin", ["OPENAI_API_KEY"] = "sk-x", ["CODEX_API_KEY"] = "y", ["OPENAI_BASE_URL"] = "https://evil.example",
            ["CODEX_HOME"] = "/home/owner/.codex", ["ANTHROPIC_API_KEY"] = "z", ["HTTPS_PROXY"] = "http://proxy:8080",
        };
        var paths = new CodexPaths("/data/codex");
        var env = CodexCommand.Environment(parent, paths);
        foreach (var banned in new[] { "OPENAI_API_KEY", "CODEX_API_KEY", "OPENAI_BASE_URL", "ANTHROPIC_API_KEY" })
            Assert.IsFalse(env.ContainsKey(banned), banned);
        Assert.AreEqual(paths.Home, env["CODEX_HOME"]);
        Assert.AreEqual(paths.User, env["HOME"]);
        Assert.AreEqual("http://proxy:8080", env["HTTPS_PROXY"]);
        Assert.IsFalse(paths.SweptDirectories.Contains(paths.Home), "the sign-in folder is never swept");

        // Windows names are matched without regard to case: Git Bash exports SYSTEMROOT and PowerShell has Path, and a
        // child without SystemRoot cannot open a connection on Windows (measured).
        var shouted = CodexCommand.Environment(new Hashtable { ["SYSTEMROOT"] = @"C:\Windows", ["Path"] = @"C:\bin" }, paths);
        Assert.AreEqual(@"C:\Windows", shouted["SystemRoot"]);
        Assert.AreEqual(@"C:\bin", shouted["PATH"]);

        // A Unix-style TMPDIR from a shell like Git Bash is never passed on Windows, where programs read TEMP/TMP.
        var withTmp = CodexCommand.Environment(new Hashtable { ["TMPDIR"] = "/c/Users/x/Temp", ["TERM"] = "xterm" }, paths);
        Assert.AreEqual(!OperatingSystem.IsWindows(), withTmp.ContainsKey("TMPDIR"));
        Assert.AreEqual(!OperatingSystem.IsWindows(), withTmp.ContainsKey("TERM"));
    }

    // ── Events ────────────────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Parses_the_measured_events()
    {
        Assert.AreEqual("t-1", ((CodexThreadStarted)CodexStream.Parse(ThreadStarted)!).ThreadId);
        Assert.IsNull(CodexStream.Parse(TurnStarted));
        Assert.AreEqual("ok", ((CodexAgentMessage)CodexStream.Parse(Agent("ok"))!).Text);
        var done = (CodexTurnCompleted)CodexStream.Parse(TurnCompleted)!;
        Assert.AreEqual(14637, done.InputTokens);
        Assert.AreEqual(5, done.OutputTokens);
        Assert.AreEqual("command_execution", ((CodexToolAttempt)CodexStream.Parse(CommandStarted)!).ItemType);
        Assert.AreEqual("boom", ((CodexFailed)CodexStream.Parse("""{"type":"turn.failed","error":{"message":"boom"}}""")!).Message);
        Assert.AreEqual("bad", ((CodexFailed)CodexStream.Parse("""{"type":"error","message":"bad"}""")!).Message);
        Assert.IsNull(CodexStream.Parse("not json"));
        Assert.IsNull(CodexStream.Parse("""{"type":"something.new"}"""));
        Assert.IsNull(CodexStream.Parse("""{"type":"item.completed","item":{"type":"reasoning","text":"secret"}}"""));
    }

    // ── The chat client ───────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Answers_with_the_last_agent_message_never_a_preamble_or_tool_output()
    {
        using var h = new Harness();
        var runner = FakeRunner.Lines(ThreadStarted, TurnStarted, Agent("I'll read the file."), CommandStarted, CommandCompleted,
            Agent("NO_TOOLS", "item_2"), TurnCompleted);
        var reply = await h.Client(runner).GetResponseAsync(Hello);
        Assert.AreEqual("NO_TOOLS", reply.Text);
        Assert.IsFalse(reply.Text.Contains("canary"));
        Assert.AreEqual(14637, reply.Usage!.InputTokenCount);
        Assert.AreEqual(ChatFinishReason.Stop, reply.FinishReason);
        Assert.AreEqual("default", h.Activity.LastModel);

        var call = runner.Calls.Single();
        StringAssert.StartsWith(call.StandardInput, "<instructions>\nBe brief.\n</instructions>");
        StringAssert.EndsWith(call.StandardInput, "Say ok");
        Assert.AreEqual(h.Paths.Home, call.Environment["CODEX_HOME"]);
    }

    [TestMethod]
    public async Task A_single_message_with_no_instructions_is_sent_as_is_with_the_chosen_model()
    {
        using var h = new Harness();
        h.Store.Save("gpt-x", "low");
        var runner = FakeRunner.Lines(ThreadStarted, Agent("ok"), TurnCompleted);
        await h.Client(runner).GetResponseAsync([new ChatMessage(ChatRole.User, "سلام")]);
        var call = runner.Calls.Single();
        Assert.AreEqual("سلام", call.StandardInput);
        Assert.AreEqual("gpt-x", call.Arguments[call.Arguments.ToList().IndexOf("-m") + 1]);
        Assert.IsTrue(call.Arguments.Contains("model_reasoning_effort=\"low\""));
    }

    [TestMethod]
    public async Task Json_without_a_schema_is_asked_for_in_words_and_a_schema_goes_to_a_file()
    {
        using var h = new Harness();
        var plain = FakeRunner.Lines(Agent("```json\n{\"a\":1}\n```"), TurnCompleted);
        var reply = await h.Client(plain).GetResponseAsync(Hello, new ChatOptions { ResponseFormat = ChatResponseFormat.Json });
        Assert.AreEqual("{\"a\":1}", reply.Text);
        StringAssert.Contains(plain.Calls.Single().StandardInput, SubscriptionPrompt.JsonInstruction);
        Assert.IsFalse(plain.Calls.Single().Arguments.Contains("--output-schema"));

        string? schemaSeen = null;
        var withSchema = new FakeRunner(inv =>
        {
            schemaSeen = File.ReadAllText(inv.Arguments[inv.Arguments.ToList().IndexOf("--output-schema") + 1]);
            return FakeRunner.Yield([Agent("{\"verdict\":\"HOLD\"}"), TurnCompleted], null);
        });
        var schema = JsonDocument.Parse("""{"type":"object","properties":{"verdict":{"type":"string"}}}""").RootElement;
        var structured = await h.Client(withSchema).GetResponseAsync(Hello, new ChatOptions { ResponseFormat = ChatResponseFormat.ForJsonSchema(schema) });
        Assert.AreEqual("{\"verdict\":\"HOLD\"}", structured.Text);
        StringAssert.Contains(schemaSeen, "verdict");
        Assert.IsFalse(Directory.EnumerateFiles(h.Paths.Work, "schema-*").Any(), "the schema file is removed after the call");
    }

    [TestMethod]
    public async Task Failures_are_classified_and_never_retried()
    {
        using var h = new Harness();
        var expired = FakeRunner.Lines(ThreadStarted, """{"type":"turn.failed","error":{"message":"401 Unauthorized: your refresh token has expired"}}""");
        var ex = await Assert.ThrowsExactlyAsync<SubscriptionCliException>(() => h.Client(expired).GetResponseAsync(Hello));
        Assert.AreEqual(SubscriptionFailure.ReauthenticationRequired, ex.Kind);
        Assert.IsFalse(ex.Retryable);

        var limited = FakeRunner.Lines("""{"type":"error","message":"You've hit your usage limit."}""");
        ex = await Assert.ThrowsExactlyAsync<SubscriptionCliException>(() => h.Client(limited).GetResponseAsync(Hello));
        Assert.AreEqual(SubscriptionFailure.RateLimited, ex.Kind);
        Assert.AreEqual(1, limited.Calls.Count);

        var missing = FakeRunner.LinesThenFail(new CliProcessException("not found") { NotInstalled = true });
        ex = await Assert.ThrowsExactlyAsync<SubscriptionCliException>(() => h.Client(missing).GetResponseAsync(Hello));
        Assert.AreEqual(SubscriptionFailure.NotInstalled, ex.Kind);

        var nothing = FakeRunner.LinesThenFail(new CliProcessException("exit 1", 1, "boom eyJabcdefghijkl.abcdefghijklmn.sig"), ThreadStarted);
        ex = await Assert.ThrowsExactlyAsync<SubscriptionCliException>(() => h.Client(nothing).GetResponseAsync(Hello));
        Assert.AreEqual(SubscriptionFailure.Failed, ex.Kind);
        Assert.IsFalse(ex.Message.Contains("eyJabcdefghijkl."), "a token-shaped string is masked");
    }

    [TestMethod]
    public async Task Not_signed_in_is_reported_before_any_process_starts()
    {
        using var h = new Harness(signedIn: false);
        var runner = FakeRunner.Lines();
        var ex = await Assert.ThrowsExactlyAsync<SubscriptionCliException>(() => h.Client(runner).GetResponseAsync(Hello));
        Assert.AreEqual(SubscriptionFailure.NotSignedIn, ex.Kind);
        Assert.AreEqual(0, runner.Calls.Count);
        Assert.IsFalse(h.Client(runner).IsSignedIn);
    }

    // ── Sign-in text ──────────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Reads_the_device_link_and_code_through_colour_codes()
    {
        const string raw = "Welcome to Codex [v\x1b[90m0.172.0\x1b[0m]\r\n\r\n" +
                           "1. Open this link in your browser and sign in to your account\r\n   \x1b[94mhttps://auth.openai.com/codex/device\x1b[0m\r\n\r\n" +
                           "2. Enter this one-time code \x1b[90m(expires in 15 minutes)\x1b[0m\r\n   \x1b[94mABCD-EFGHI\x1b[0m\r\n";
        var (link, code) = CodexDeviceText.Parse(raw);
        Assert.AreEqual("https://auth.openai.com/codex/device", link);
        Assert.AreEqual("ABCD-EFGHI", code);
        Assert.IsFalse(CodexDeviceText.SaysSignedIn(raw));
        Assert.IsTrue(CodexDeviceText.SaysSignedIn(raw + "\r\nSuccessfully logged in\r\n"));
        Assert.AreEqual((null, null), CodexDeviceText.Parse("Welcome to Codex"));
    }

    // ── Routing across both providers ─────────────────────────────────────────────────────────────

    private sealed class Provider(string id, bool signedIn, IChatClient client) : ISubscriptionProvider
    {
        public string Id => id;
        public string DisplayName => id;
        public bool IsSignedIn { get; set; } = signedIn;
        public bool IsInstalled { get; set; } = true;
        public IChatClient Client => client;
    }

    private sealed class Policy(Func<string, SubscriptionAccess> read) : ISubscriptionAccessPolicy
    {
        public Task<SubscriptionAccess> ReadAsync(string provider, CancellationToken cancellationToken = default) => Task.FromResult(read(provider));
    }

    [TestMethod]
    public async Task Each_purpose_uses_its_chosen_provider_and_falls_back_when_it_cannot()
    {
        using var h = new Harness();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["LLM:Provider"] = "ollama" }).Build();
        using var configured = new RuntimeModelChatClient(config);
        var codex = new Provider(AiProviders.Codex, signedIn: true, h.Client(FakeRunner.Lines(Agent("from codex"), TurnCompleted)));
        var claude = new Provider(AiProviders.ClaudeCode, signedIn: false, new ChatClientStub("from claude"));
        var denied = new HashSet<string>();
        using var services = new ServiceCollection()
            .AddSingleton<ISubscriptionAccessPolicy>(new Policy(p => new SubscriptionAccess(!denied.Contains(p)))).BuildServiceProvider();
        var routing = new AiRoutingStore(Path.Combine(h.Dir, "routing.json"), _ => null, NullLogger<AiRoutingStore>.Instance);
        var router = new AiClientRouter(configured, [claude, codex], routing, services);

        routing.Set(AiPurposes.Chat, AiProviders.Codex);
        Assert.AreEqual(AiProviders.Codex, await router.ProviderInUseAsync(AiPurposes.Chat, default));
        Assert.AreEqual("from codex", (await router.For(AiPurposes.Chat).GetResponseAsync(Hello)).Text);

        // Claude is the default for assessments but not signed in: the configured AI answers instead.
        Assert.AreEqual(AiProviders.Configured, await router.ProviderInUseAsync(AiPurposes.StockAssessment, default));
        claude.IsSignedIn = true;
        Assert.AreEqual(AiProviders.ClaudeCode, await router.ProviderInUseAsync(AiPurposes.StockAssessment, default));
        Assert.AreEqual("from claude", (await router.For(AiPurposes.StockAssessment).GetResponseAsync(Hello)).Text);

        // A release built without the CLI leaves the sign-in on the volume: the purpose falls back, never fails.
        codex.IsInstalled = false;
        Assert.AreEqual(AiProviders.Configured, await router.ProviderInUseAsync(AiPurposes.Chat, default));
        Assert.IsFalse(await router.UsesSubscriptionAsync(AiPurposes.Chat, default));
        codex.IsInstalled = true;
        Assert.AreEqual(AiProviders.Codex, await router.ProviderInUseAsync(AiPurposes.Chat, default));

        // A hosted operator switches one provider off: only that one stops being used.
        denied.Add(AiProviders.Codex);
        Assert.AreEqual(AiProviders.Configured, await router.ProviderInUseAsync(AiPurposes.Chat, default));
        Assert.AreEqual(AiProviders.ClaudeCode, await router.ProviderInUseAsync(AiPurposes.StockAssessment, default));
        Assert.IsTrue(await router.UsesSubscriptionAsync(AiPurposes.StockAssessment, default));
        Assert.IsFalse(await router.UsesSubscriptionAsync(AiPurposes.Chat, default));
    }

    [TestMethod]
    public void A_cli_counts_as_installed_only_where_a_process_start_would_find_it()
    {
        using var h = new Harness();
        var bin = Directory.CreateDirectory(Path.Combine(h.Dir, "bin")).FullName;
        var exe = Path.Combine(bin, "codex");
        File.WriteAllText(exe, "");
        File.WriteAllText(Path.Combine(bin, "claude.cmd"), "");
        File.WriteAllText(Path.Combine(bin, "tool.exe"), "");
        var path = string.Join(Path.PathSeparator, Path.Combine(h.Dir, "missing"), bin);

        Assert.AreEqual(exe, CliExecutable.Find(exe, null, windows: false));                 // a path: as given
        Assert.IsNull(CliExecutable.Find(Path.Combine(bin, "gone"), path, windows: false));  // the image's path, CLI left out
        Assert.AreEqual(exe, CliExecutable.Find("codex", path, windows: false));             // a bare name: on PATH
        Assert.IsNull(CliExecutable.Find("codex", null, windows: false));
        Assert.AreEqual(Path.Combine(bin, "tool.exe"), CliExecutable.Find("tool", path, windows: true)); // Windows adds .exe
        Assert.IsNull(CliExecutable.Find("tool", path, windows: false));
        Assert.IsNull(CliExecutable.Find("claude", path, windows: true));                    // a .cmd shim is never started
        Assert.IsFalse(CliExecutable.IsInstalled(""));
    }

    private sealed class ChatClientStub(string text) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>Writes what was passed to the CLI and what came back (environment NAMES, arguments, output lines) when
    /// CODEX_IT_DUMP names a file. Manual diagnosis only.</summary>
    private sealed class RecordingRunner(ICliProcessRunner inner, string? dump) : ICliProcessRunner
    {
        public async IAsyncEnumerable<string> RunAsync(CliInvocation invocation,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var log = new List<string> { "args: " + string.Join(" ", invocation.Arguments), "cwd: " + invocation.WorkingDirectory };
            log.AddRange(invocation.Environment.Select(kv => $"env {kv.Key}={(kv.Key.Contains("PATH") ? kv.Value : "<set>")}"));
            try
            {
                await foreach (var line in inner.RunAsync(invocation, cancellationToken))
                {
                    log.Add("out: " + line);
                    yield return line;
                }
            }
            finally
            {
                if (dump is not null) File.WriteAllLines(dump, log);
            }
        }
    }

    /// <summary>Manual only: set CODEX_IT_HOME to a CODEX_HOME holding a real sign-in (and Codex on PATH or
    /// CODEX_IT_EXE). Makes one tiny call on the plan; never runs in CI.</summary>
    [TestMethod, TestCategory("Manual")]
    public async Task Real_cli_answers_with_a_real_sign_in()
    {
        var home = Environment.GetEnvironmentVariable("CODEX_IT_HOME");
        if (string.IsNullOrWhiteSpace(home)) { Assert.Inconclusive("Set CODEX_IT_HOME to run."); return; }
        var root = Path.Combine(Path.GetDirectoryName(home.TrimEnd('\\', '/'))!, "codex-it-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = new CodexPaths(root);
        paths.Ensure();
        try
        {
            File.Copy(Path.Combine(home, "auth.json"), paths.AuthFile);
            var options = new CodexOptions { ExecutablePath = Environment.GetEnvironmentVariable("CODEX_IT_EXE") ?? "codex" };
            var store = new CodexStore(Path.Combine(root, "codex.json"), NullLogger<CodexStore>.Instance);
            var runner = new RecordingRunner(new CliProcessRunner(), Environment.GetEnvironmentVariable("CODEX_IT_DUMP"));
            var client = new CodexChatClient(options, paths, store, runner, new CodexActivity(), NullLogger<CodexChatClient>.Instance);
            var (passed, message) = await client.TestAsync(default);
            Assert.IsTrue(passed, message);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
