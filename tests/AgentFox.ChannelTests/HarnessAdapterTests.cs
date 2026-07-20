using AgentFox.Agents;
using AgentFox.Harness;
using AgentFox.Plugins.Interfaces;
using AgentFox.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace AgentFox.ChannelTests;

/// <summary>
/// Phase 0 safety contract for the HarnessAgent adapter: disabled by default with no
/// behaviour change, and every bridged tool executes through the AgentFox gateway so the
/// approval gate cannot be bypassed from a Harness profile.
/// </summary>
[TestClass]
public sealed class HarnessAdapterTests
{
    [TestMethod]
    public void Factory_IsDisabledByDefault_AndRefusesToCreateAgents()
    {
        var factory = new HarnessAgentFactory(Options.Create(new HarnessOptions()));

        Assert.IsFalse(factory.IsEnabled);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            factory.Create(new StubChatClient(), new AgentBuilder(new ToolRegistry())));
    }

    [TestMethod]
    public void Factory_RejectsUnknownProfiles()
    {
        var factory = new HarnessAgentFactory(Options.Create(new HarnessOptions { Enabled = true }));

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            factory.Create(new StubChatClient(), new AgentBuilder(new ToolRegistry()), "does-not-exist"));
        StringAssert.Contains(ex.Message, "Unknown Harness profile");
    }

    [TestMethod]
    public void Options_SeedTheThreeRoadmapProfilesWithLeastPrivilege()
    {
        var options = new HarnessOptions();

        Assert.IsFalse(options.Enabled);
        Assert.AreEqual(HarnessOptions.MainSafeProfile, options.DefaultProfile);
        CollectionAssert.IsSubsetOf(
            new[]
            {
                HarnessOptions.MainSafeProfile,
                HarnessOptions.TradingResearchProfile,
                HarnessOptions.DeveloperSandboxProfile
            },
            options.Profiles.Keys.ToArray());

        var mainSafe = options.Profiles[HarnessOptions.MainSafeProfile];
        Assert.IsFalse(mainSafe.EnableTodoAndModes);
        Assert.IsFalse(mainSafe.EnableOpenTelemetry);
        Assert.IsFalse(mainSafe.EnableHarnessCompaction);
    }

    [TestMethod]
    public void Factory_CreatesAnAgentForTheDefaultProfileWhenEnabled()
    {
        var factory = new HarnessAgentFactory(Options.Create(new HarnessOptions { Enabled = true }));
        var registry = new ToolRegistry();
        registry.Register(new FakeMutatingTool());

        var agent = factory.Create(new StubChatClient(), new AgentBuilder(registry));

        Assert.IsNotNull(agent);
        StringAssert.Contains(agent.Name, HarnessOptions.MainSafeProfile);
    }

    [TestMethod]
    public async Task Gateway_BlocksToolWhenApprovalGateDenies()
    {
        var registry = new ToolRegistry();
        var tool = new FakeMutatingTool();
        registry.Register(tool);
        var builder = new AgentBuilder(registry)
            .WithToolApprovalGate((_, _, _) => Task.FromResult(false));

        var result = await builder.ExecuteThroughGatewayAsync(tool.Name, new Dictionary<string, object?>());

        Assert.IsFalse(result.Success);
        Assert.IsFalse(tool.Executed);
        StringAssert.Contains(result.Error, "blocked");
    }

    [TestMethod]
    public async Task Gateway_ExecutesToolWhenApprovalGateAllows()
    {
        var registry = new ToolRegistry();
        var tool = new FakeMutatingTool();
        registry.Register(tool);
        var builder = new AgentBuilder(registry)
            .WithToolApprovalGate((_, _, _) => Task.FromResult(true));

        var result = await builder.ExecuteThroughGatewayAsync(tool.Name, new Dictionary<string, object?>());

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsTrue(tool.Executed);
    }

    [TestMethod]
    public async Task BridgedFunction_CannotBypassTheApprovalGate()
    {
        var registry = new ToolRegistry();
        var tool = new FakeMutatingTool();
        registry.Register(tool);
        var builder = new AgentBuilder(registry)
            .WithToolApprovalGate((_, _, _) => Task.FromResult(false));

        var bridged = builder.CreateGatewayTools().OfType<AIFunction>().ToList();
        var function = bridged.Single(f => f.Name == tool.Name);

        await function.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        Assert.IsFalse(tool.Executed,
            "A bridged Harness tool executed despite the AgentFox approval gate denying it.");
    }

    // ── Fakes ─────────────────────────────────────────────────────────────────

    private sealed class FakeMutatingTool : ITool
    {
        public bool Executed { get; private set; }
        public string Name => "fake_mutating_tool";
        public string Description => "Test tool that records whether it was executed.";
        public Dictionary<string, ToolParameter> Parameters => new()
        {
            ["value"] = new() { Type = "string", Description = "Any value.", Required = false }
        };

        public Task<ToolResult> ExecuteAsync(Dictionary<string, object?> arguments)
        {
            Executed = true;
            return Task.FromResult(ToolResult.Ok("executed"));
        }
    }

    private sealed class StubChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => EmptyUpdates();

        private static async IAsyncEnumerable<ChatResponseUpdate> EmptyUpdates()
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
