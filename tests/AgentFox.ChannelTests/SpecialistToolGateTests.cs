using AgentFox.Agents;
using AgentFox.Plugins.Interfaces;
using AgentFox.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TradingAgent;

namespace AgentFox.ChannelTests;

/// <summary>
/// Per-turn availability of a specialist's tools (<see cref="SpecialistAgentDescriptor.ToolGates"/>). Asserted against the
/// REAL <c>DynamicAgentMiddleware</c> and the real gateway entry point, with a stub model that records what it was offered.
/// The cases that matter: nothing changes by default, a closed gate hides the tool AND the prose that names it, a throwing
/// gate fails closed for that tool alone, nothing is cached across calls, and a hidden tool that is called anyway is refused.
/// </summary>
[TestClass]
public sealed class SpecialistToolGateTests
{
    private const string Gated = "rebuy_context";
    private const string Other = "get_portfolio";

    private sealed class RecordingChatClient : IChatClient
    {
        public ChatOptions? Seen { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Seen = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Seen = options;
            await Task.CompletedTask;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static ChatOptions Offer() => new()
    {
        Instructions = "base",
        Tools =
        [
            AIFunctionFactory.Create(() => "x", Gated),
            AIFunctionFactory.Create(() => "y", Other),
        ]
    };

    private static DynamicAgentMiddleware Middleware(
        RecordingChatClient inner,
        IReadOnlyDictionary<string, Func<CancellationToken, ValueTask<bool>>>? gates,
        Func<IReadOnlySet<string>, string?>? prompt = null) =>
        new(inner, new ToolRegistry(), new PromptContributorRegistry(), [], _ => throw new InvalidOperationException(),
            toolGates: gates, gatedPromptFragment: prompt);

    private static string[] Names(ChatOptions? o) => (o?.Tools ?? []).Select(t => t.Name).OrderBy(n => n).ToArray();

    private static Dictionary<string, Func<CancellationToken, ValueTask<bool>>> Gate(Func<CancellationToken, ValueTask<bool>> f) =>
        new(StringComparer.OrdinalIgnoreCase) { [Gated] = f };

    [TestMethod]
    public async Task Without_gates_the_model_is_offered_exactly_what_it_was_before()
    {
        var inner = new RecordingChatClient();
        var options = Offer();
        await Middleware(inner, gates: null).GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options);

        CollectionAssert.AreEqual(new[] { Other, Gated }, Names(inner.Seen));
        Assert.AreEqual("base", inner.Seen!.Instructions);
    }

    [TestMethod]
    public async Task A_closed_gate_hides_the_tool_and_the_prose_that_names_it()
    {
        var inner = new RecordingChatClient();
        var options = Offer();
        IReadOnlySet<string>? offered = null;
        var middleware = Middleware(inner, Gate(_ => ValueTask.FromResult(false)), names =>
        {
            offered = names;
            return names.Contains(Gated) ? "Call rebuy_context for rebuy questions." : null;
        });

        await middleware.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options);

        CollectionAssert.AreEqual(new[] { Other }, Names(inner.Seen));
        Assert.IsFalse(offered!.Contains(Gated));
        Assert.IsTrue(offered.Contains(Other));
        Assert.AreEqual("base", inner.Seen!.Instructions);
        // The options the outer function-invoker owns keep the tool, so a call to it still reaches the host's own refusal.
        CollectionAssert.AreEqual(new[] { Other, Gated }, Names(options));
    }

    [TestMethod]
    public async Task An_open_gate_offers_the_tool_and_adds_the_prose()
    {
        var inner = new RecordingChatClient();
        var middleware = Middleware(inner, Gate(_ => ValueTask.FromResult(true)),
            names => names.Contains(Gated) ? "Call rebuy_context for rebuy questions." : null);

        await middleware.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], Offer());

        CollectionAssert.AreEqual(new[] { Other, Gated }, Names(inner.Seen));
        Assert.AreEqual("base\n\nCall rebuy_context for rebuy questions.", inner.Seen!.Instructions);
    }

    [TestMethod]
    public async Task A_throwing_gate_hides_only_its_own_tool_and_the_turn_continues()
    {
        var inner = new RecordingChatClient();
        var middleware = Middleware(inner, Gate(_ => throw new InvalidOperationException("hub unreachable")));

        var response = await middleware.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], Offer());

        Assert.AreEqual("ok", response.Text);
        CollectionAssert.AreEqual(new[] { Other }, Names(inner.Seen));
    }

    [TestMethod]
    public async Task A_gate_that_fails_asynchronously_also_fails_closed()
    {
        var inner = new RecordingChatClient();
        var middleware = Middleware(inner, Gate(async _ =>
        {
            await Task.Yield();
            throw new TimeoutException();
        }));

        await middleware.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], Offer());

        CollectionAssert.AreEqual(new[] { Other }, Names(inner.Seen));
    }

    [TestMethod]
    public async Task A_throwing_prompt_provider_costs_its_text_and_not_the_call()
    {
        var inner = new RecordingChatClient();
        var middleware = Middleware(inner, gates: null, prompt: _ => throw new InvalidOperationException());

        var response = await middleware.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], Offer());

        Assert.AreEqual("ok", response.Text);
        Assert.AreEqual("base", inner.Seen!.Instructions);
    }

    [TestMethod]
    public async Task Availability_is_asked_on_every_call_and_never_cached()
    {
        var inner = new RecordingChatClient();
        var open = true;
        var asked = 0;
        var middleware = Middleware(inner, Gate(_ => { asked++; return ValueTask.FromResult(open); }));

        await middleware.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], Offer());
        CollectionAssert.AreEqual(new[] { Other, Gated }, Names(inner.Seen));
        open = false;
        await middleware.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], Offer());
        CollectionAssert.AreEqual(new[] { Other }, Names(inner.Seen));
        open = true;
        await middleware.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], Offer());
        CollectionAssert.AreEqual(new[] { Other, Gated }, Names(inner.Seen));

        Assert.AreEqual(3, asked);
    }

    [TestMethod]
    public async Task The_streaming_path_applies_the_same_gate()
    {
        var inner = new RecordingChatClient();
        var middleware = Middleware(inner, Gate(_ => ValueTask.FromResult(false)));

        await foreach (var _ in middleware.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], Offer())) { }

        CollectionAssert.AreEqual(new[] { Other }, Names(inner.Seen));
    }

    [TestMethod]
    public async Task A_cancelled_caller_is_not_mistaken_for_a_failed_gate()
    {
        using var cts = new CancellationTokenSource();
        var middleware = Middleware(new RecordingChatClient(), Gate(ct =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(true);
        }));

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await middleware.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], Offer(), cts.Token));
    }

    [TestMethod]
    public async Task A_hidden_tool_that_is_called_anyway_is_refused_and_never_runs()
    {
        var registry = new ToolRegistry();
        var tool = new CountingTool(Gated);
        registry.Register(tool);
        var open = false;
        var builder = new AgentBuilder(registry).WithToolGates(Gate(_ => ValueTask.FromResult(open)));

        var refused = await builder.ExecuteThroughGatewayAsync(Gated, new Dictionary<string, object?>());
        Assert.IsFalse(refused.Success);
        StringAssert.Contains(refused.Error, "not available");
        Assert.AreEqual(0, tool.Runs);

        open = true;
        var allowed = await builder.ExecuteThroughGatewayAsync(Gated, new Dictionary<string, object?>());
        Assert.IsTrue(allowed.Success, allowed.Error);
        Assert.AreEqual(1, tool.Runs);
    }

    [TestMethod]
    public async Task A_throwing_gate_refuses_the_call_too()
    {
        var registry = new ToolRegistry();
        var tool = new CountingTool(Gated);
        registry.Register(tool);
        var builder = new AgentBuilder(registry).WithToolGates(Gate(_ => throw new InvalidOperationException()));

        var result = await builder.ExecuteThroughGatewayAsync(Gated, new Dictionary<string, object?>());

        Assert.IsFalse(result.Success);
        Assert.AreEqual(0, tool.Runs);
    }

    [TestMethod]
    public async Task An_ungated_tool_is_untouched_by_gates_on_others()
    {
        var registry = new ToolRegistry();
        var tool = new CountingTool(Other);
        registry.Register(tool);
        var builder = new AgentBuilder(registry).WithToolGates(Gate(_ => ValueTask.FromResult(false)));

        var result = await builder.ExecuteThroughGatewayAsync(Other, new Dictionary<string, object?>());

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(1, tool.Runs);
    }

    [TestMethod]
    public void The_community_composition_declares_no_gates()
    {
        var community = TradingCompositionOptions.Community;
        Assert.AreEqual(0, community.SpecialistToolAvailability.Count);
        Assert.IsNull(community.SpecialistPromptAppendixProvider);
        Assert.IsNull(TradingAgentRuntime.BindToolGates(community, new ServiceCollection().BuildServiceProvider()));
    }

    [TestMethod]
    public async Task An_editions_predicates_are_bound_to_the_hosts_service_provider()
    {
        var provider = new ServiceCollection().BuildServiceProvider();
        IServiceProvider? received = null;
        var composition = TradingCompositionOptions.Community with
        {
            SpecialistToolAvailability = new Dictionary<string, Func<IServiceProvider, CancellationToken, ValueTask<bool>>>
            {
                [Gated] = (services, _) => { received = services; return ValueTask.FromResult(false); }
            }
        };

        var gates = TradingAgentRuntime.BindToolGates(composition, provider)!;

        Assert.IsFalse(await gates[Gated](CancellationToken.None));
        Assert.AreSame(provider, received);
        Assert.IsTrue(gates.ContainsKey(Gated.ToUpperInvariant()), "tool names match case-insensitively");
    }

    private sealed class CountingTool(string name) : ITool
    {
        public int Runs { get; private set; }
        public string Name => name;
        public string Description => "Counts executions.";
        public Dictionary<string, ToolParameter> Parameters => new();

        public Task<ToolResult> ExecuteAsync(Dictionary<string, object?> arguments)
        {
            Runs++;
            return Task.FromResult(ToolResult.Ok("ran"));
        }
    }
}
