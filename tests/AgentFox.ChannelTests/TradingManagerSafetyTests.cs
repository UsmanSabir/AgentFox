using System.Security.Cryptography;
using System.Text;
using AgentFox.Agents;
using AgentFox.Plugins;
using AgentFox.Plugins.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingAgent.Broker;
using TradingAgent.Channel;
using TradingAgent.Config;
using TradingAgent.Manager;
using TradingAgent.Market;
using TradingAgent.Models;
using TradingAgent.Persistence;
using TradingAgent.Risk;
using TradingAgent.Reconciliation;

namespace AgentFox.ChannelTests;

[TestClass]
public sealed class TradingManagerSafetyTests
{
    [TestMethod]
    [DataRow("2026-07-06T04:31:00Z", false)] // Monday 09:31 PKT
    [DataRow("2026-07-06T04:32:00Z", true)]  // Monday 09:32 PKT
    [DataRow("2026-07-10T06:59:00Z", true)]  // Friday 11:59 PKT
    [DataRow("2026-07-10T07:30:00Z", false)] // Friday break 12:30 PKT
    [DataRow("2026-07-10T09:32:00Z", true)]  // Friday second session 14:32 PKT
    [DataRow("2026-07-10T11:30:00Z", false)] // Friday close 16:30 PKT
    public void PsxCalendar_EnforcesPublishedRegularSessions(string utc, bool expectedOpen)
    {
        var calendar = Calendar(new TradingAgentOptions());
        var status = calendar.GetStatus(DateTime.Parse(utc).ToUniversalTime());
        Assert.AreEqual(expectedOpen, status.IsOpen, status.Reason);
    }

    [TestMethod]
    public void PsxCalendar_ConfiguredHolidayFailsClosed()
    {
        var calendar = Calendar(new TradingAgentOptions
        {
            MarketHolidays = ["2026-07-06"]
        });
        var status = calendar.GetStatus(DateTime.Parse("2026-07-06T06:00:00Z").ToUniversalTime());
        Assert.IsFalse(status.IsOpen);
        StringAssert.Contains(status.Reason, "holiday");
    }

    [TestMethod]
    public async Task WhatsAppWebhook_RequiresValidSignatureAndRejectsReplay()
    {
        const string secret = "test-secret-with-enough-entropy";
        var channel = new WhatsAppBridgeChannel(
            callbackUrl: null,
            groupFilter: "PSX Signals",
            NullLogger.Instance,
            requireSignature: true,
            webhookSecret: secret,
            maxClockSkewSeconds: 120,
            allowedSenders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "sender-1" });
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var body = """
            {"id":"message-1","from":"sender-1","group":"PSX Signals","body":"BUY OGDC @ 165"}
            """;
        var signature = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}")));
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-AgentFox-Timestamp"] = timestamp,
            ["X-AgentFox-Signature"] = $"sha256={signature}"
        };

        var accepted = await channel.ProcessWebhookAsync(body, headers);
        Assert.IsTrue(accepted.Accepted, accepted.Error);

        var replay = await channel.ProcessWebhookAsync(body, headers);
        Assert.IsFalse(replay.Accepted);
        StringAssert.Contains(replay.Error, "Replayed");

        headers["X-AgentFox-Signature"] = "sha256=00";
        var forged = await channel.ProcessWebhookAsync(body.Replace("message-1", "message-2"), headers);
        Assert.IsFalse(forged.Accepted);
    }

    [TestMethod]
    public async Task SqliteLedger_ClaimsIdempotencyKeyOnlyOnce()
    {
        var temp = TempDirectory();
        try
        {
            var options = Options.Create(new TradingAgentOptions
            {
                DatabasePath = Path.Combine(temp, "trading.db")
            });
            var repository = new SqliteTradingRepository(
                options,
                new ConfigurationBuilder().Build(),
                NullLogger<SqliteTradingRepository>.Instance);

            var first = await repository.TryBeginExecutionAsync("same-key", "{}", "v1");
            var second = await repository.TryBeginExecutionAsync("same-key", "{}", "v1");

            Assert.IsTrue(first.Acquired);
            Assert.IsFalse(second.Acquired);
            Assert.AreEqual(first.ExecutionId, second.ExecutionId);

            var concurrent = await Task.WhenAll(Enumerable.Range(0, 8)
                .Select(_ => repository.TryBeginExecutionAsync("parallel-key", "{}", "v1")));
            Assert.AreEqual(1, concurrent.Count(x => x.Acquired));
            Assert.AreEqual(1, concurrent.Select(x => x.ExecutionId).Distinct().Count());
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [TestMethod]
    public async Task SqliteLedger_ExposesTypedOperationalReadModel()
    {
        var temp = TempDirectory();
        try
        {
            var options = Options.Create(new TradingAgentOptions
            {
                DatabasePath = Path.Combine(temp, "trading.db")
            });
            var repository = new SqliteTradingRepository(
                options, new ConfigurationBuilder().Build(), NullLogger<SqliteTradingRepository>.Instance);

            var proposalId = await repository.CreateProposalAsync(
                "proposal-key", "{\"orders\":[{\"symbol\":\"OGDC\"}]}", "policy-1");
            var claim = await repository.TryBeginExecutionAsync(
                "execution-key", "{\"symbol\":\"OGDC\"}", "policy-1");
            await repository.CompleteExecutionAsync(claim.ExecutionId, "accepted", "{\"success\":true}");
            await repository.AppendEventAsync(claim.ExecutionId, "accepted", "{\"brokerOrderId\":\"paper-1\"}");
            await repository.RecordReconciliationAsync(new BrokerReconciliationSnapshot(
                true, true, "ok", DateTime.UtcNow, "{\"positions\":0}"));

            var proposals = await repository.GetProposalsAsync();
            var executions = await repository.GetExecutionsAsync();
            var events = await repository.GetEventsAsync();
            var reconciliations = await repository.GetReconciliationRunsAsync();

            Assert.AreEqual(proposalId, proposals.Single().ProposalId);
            Assert.AreEqual("OGDC", proposals.Single().Proposal.GetProperty("orders")[0].GetProperty("symbol").GetString());
            Assert.AreEqual("accepted", executions.Single().State);
            Assert.IsTrue(executions.Single().Result?.GetProperty("success").GetBoolean());
            Assert.AreEqual("accepted", events.Single().EventType);
            Assert.AreEqual("healthy", reconciliations.Single().State);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [TestMethod]
    public async Task TradingManager_PaperModePersistsWithoutCallingBroker()
    {
        var temp = TempDirectory();
        try
        {
            var configured = new TradingAgentOptions
            {
                AutoExecute = true,
                ExecutionMode = "Paper",
                DatabasePath = Path.Combine(temp, "trading.db"),
                AllowedSymbols = ["OGDC"]
            };
            var options = Options.Create(configured);
            var pluginConfig = new PluginConfigManager(
                Path.Combine(temp, "plugin-config"), NullLogger<PluginConfigManager>.Instance);
            var policy = new TradingPolicyProvider(options, pluginConfig);
            var repository = new SqliteTradingRepository(
                options, new ConfigurationBuilder().Build(), NullLogger<SqliteTradingRepository>.Instance);
            var broker = new RecordingBroker();
            var manager = new TradingAgent.Manager.TradingManager(
                broker, repository, new AlwaysOpenCalendar(), policy,
                new TradingRiskEngine(Options.Create(new AhkConfig()), options),
                new TradingReconciliationState(), new ApprovalIntentRegistry(), options,
                NullLogger<TradingAgent.Manager.TradingManager>.Instance);
            IReadOnlyList<IReadOnlyList<TradingSignal>> groups =
            [
                new List<TradingSignal>
                {
                    new() { Action = "BUY", Symbol = "OGDC", Quantity = 10, EntryPrice = 100, OrderType = "LIMIT" }
                }
            ];

            var first = await manager.ExecuteGroupsAsync(groups, "source-message-1");
            var replay = await manager.ExecuteGroupsAsync(groups, "source-message-1");

            Assert.IsTrue(first.Executed);
            Assert.IsFalse(broker.WasCalled);
            Assert.IsTrue(replay.IsReplay);
            Assert.AreEqual(first.ExecutionId, replay.ExecutionId);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [TestMethod]
    public void SpecialistRegistry_ResolvesDedicatedTradingChannel()
    {
        IAgentRegistry registry = new SpecialistAgentRegistry();
        registry.Register(new SpecialistAgentDescriptor
        {
            Id = "trading-agent",
            Name = "Trading",
            SystemPrompt = "Trading specialist",
            ChannelTypes = ["whatsapp-bridge"]
        });

        Assert.AreEqual("trading-agent", registry.ResolveForChannel("WHATSAPP-BRIDGE")?.Id);
        Assert.IsNull(registry.ResolveForChannel("telegram"));

        var runtime = ((SpecialistAgentRegistry)registry).GetRuntimeStatuses().Single();
        Assert.IsFalse(runtime.IsActive);
        Assert.AreEqual(1, runtime.MaxConcurrentTurns);
        Assert.AreEqual(0, runtime.TotalTurns);
    }

    [TestMethod]
    public void RiskEngine_FailsClosedWithoutConfiguredUniverseOrWithKillSwitch()
    {
        IReadOnlyList<IReadOnlyList<TradingSignal>> groups =
        [
            new List<TradingSignal>
            {
                new() { Action = "BUY", Symbol = "OGDC", Quantity = 10, EntryPrice = 100, OrderType = "LIMIT" }
            }
        ];
        var noUniverse = new TradingRiskEngine(
            Options.Create(new AhkConfig()), Options.Create(new TradingAgentOptions()));
        Assert.IsFalse(noUniverse.Validate(groups).Allowed);

        var killed = new TradingRiskEngine(
            Options.Create(new AhkConfig()),
            Options.Create(new TradingAgentOptions { AllowedSymbols = ["OGDC"], KillSwitch = true }));
        var result = killed.Validate(groups);
        Assert.IsFalse(result.Allowed);
        Assert.IsTrue(result.Violations.Any(x => x.Contains("kill switch", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task SpecialistCommand_UsesDedicatedQueueLane()
    {
        var queue = new CommandQueue();
        using var processor = new CommandProcessor(queue);
        processor.RegisterLaneHandler(CommandLane.Specialist, (command, _) =>
        {
            var specialist = (SpecialistAgentCommand)command;
            specialist.ResultSource.TrySetResult($"handled:{specialist.AgentId}:{specialist.Input}");
            return Task.CompletedTask;
        });
        processor.Start();
        try
        {
            var command = new SpecialistAgentCommand
            {
                SessionKey = "specialist:test:session",
                AgentId = "trading-agent",
                Input = "status"
            };
            queue.Enqueue(command);
            var result = await command.ResultSource.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.AreEqual(CommandLane.Specialist, command.Lane);
            Assert.AreEqual("handled:trading-agent:status", result);
            var specialistLane = processor.GetLaneStatistics().Single(x => x.Lane == "Specialist");
            Assert.AreEqual(0, specialistLane.QueuedCommands);
            Assert.IsTrue(specialistLane.HandlerRegistered);
            Assert.AreEqual(3, specialistLane.MaxConcurrency);
        }
        finally
        {
            await processor.StopAsync(TimeSpan.FromSeconds(2));
        }
    }

    [TestMethod]
    public async Task LiveExecution_FailsClosedWhenBrokerReconciliationIsUnsupported()
    {
        var temp = TempDirectory();
        try
        {
            var configured = new TradingAgentOptions
            {
                AutoExecute = true,
                ExecutionMode = "ApprovalRequired",
                DatabasePath = Path.Combine(temp, "trading.db"),
                AllowedSymbols = ["OGDC"],
                RequireReconciliationHealthy = true
            };
            var options = Options.Create(configured);
            var pluginConfig = new PluginConfigManager(
                Path.Combine(temp, "plugin-config"), NullLogger<PluginConfigManager>.Instance);
            var repository = new SqliteTradingRepository(
                options, new ConfigurationBuilder().Build(), NullLogger<SqliteTradingRepository>.Instance);
            var broker = new RecordingBroker();
            var policyProvider = new TradingPolicyProvider(options, pluginConfig);
            var intentRegistry = new ApprovalIntentRegistry();
            var manager = new TradingAgent.Manager.TradingManager(
                broker, repository, new AlwaysOpenCalendar(),
                policyProvider,
                new TradingRiskEngine(Options.Create(new AhkConfig()), options),
                new TradingReconciliationState(), intentRegistry, options,
                NullLogger<TradingAgent.Manager.TradingManager>.Instance);
            IReadOnlyList<IReadOnlyList<TradingSignal>> groups =
            [
                new List<TradingSignal>
                {
                    new() { Action = "BUY", Symbol = "OGDC", Quantity = 10, EntryPrice = 100, OrderType = "LIMIT" }
                }
            ];

            // A valid approval intent so the execution reaches the reconciliation check.
            var intent = ApprovalIntent.Create(
                groups, "source-message-live", policyProvider.Current().Version, TimeSpan.FromMinutes(2));
            intentRegistry.Register(intent);

            var result = await manager.ExecuteGroupsAsync(
                groups, "source-message-live",
                ExecutionAuthorization.HostToolGate("test-approver", intent));

            Assert.IsFalse(result.Executed);
            Assert.IsFalse(broker.WasCalled);
            StringAssert.Contains(result.Reason, "reconciliation");
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    private static PsxMarketCalendar Calendar(TradingAgentOptions options) =>
        new(Options.Create(options), NullLogger<PsxMarketCalendar>.Instance);

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agentfox-trading-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class AlwaysOpenCalendar : IMarketCalendar
    {
        public MarketStatus GetStatus(DateTime? utcNow = null) =>
            new(true, DateTime.UtcNow.AddHours(5), "open");
    }

    private sealed class RecordingBroker : IBrokerAdapter
    {
        public bool WasCalled { get; private set; }

        public Task<IReadOnlyDictionary<string, decimal?>> GetMarketPricesAsync(IReadOnlyList<string> symbols) =>
            Task.FromResult<IReadOnlyDictionary<string, decimal?>>(new Dictionary<string, decimal?>());

        public Task<IReadOnlyList<IReadOnlyList<OrderResult>>> PlaceOrderGroupsAsync(
            IReadOnlyList<IReadOnlyList<TradingSignal>> groups)
        {
            WasCalled = true;
            return Task.FromResult<IReadOnlyList<IReadOnlyList<OrderResult>>>([]);
        }
    }
}
