using System.Diagnostics;
using AgentFox.Agents;
using AgentFox.Plugins.Observability;

namespace AgentFox.ChannelTests;

/// <summary>
/// Pins the correlation chain, and in particular the ONE place it does not propagate on its own.
///
/// An AsyncLocal makes most of this work for free, which is precisely why the queue boundary is
/// worth a test: it is the only hop where "it just flows" is false, it fails by producing an
/// orphaned record rather than an error, and nothing about the call site says so.
/// </summary>
[TestClass]
public sealed class CorrelationContextTests
{
    [TestMethod]
    public void NoScope_MeansNoCorrelation()
    {
        // Absence must read as absence. A default id would create a correlation group of one row
        // that looks like real evidence — see SqliteTradingRepository.CorrelationParameter.
        Assert.IsNull(CorrelationContext.Current);
    }

    [TestMethod]
    public void Begin_MintsWhenGivenNothing_AndAdoptsWhatItIsGiven()
    {
        using (CorrelationContext.Begin())
            Assert.IsFalse(string.IsNullOrWhiteSpace(CorrelationContext.Current));

        using (CorrelationContext.Begin("inbound-id"))
            Assert.AreEqual("inbound-id", CorrelationContext.Current);

        Assert.IsNull(CorrelationContext.Current, "The scope must restore what it replaced.");
    }

    [TestMethod]
    public void Begin_Nests_AndRestoresTheOuterIdOnDispose()
    {
        using (CorrelationContext.Begin("outer"))
        {
            using (CorrelationContext.Begin("inner"))
                Assert.AreEqual("inner", CorrelationContext.Current);

            Assert.AreEqual("outer", CorrelationContext.Current,
                "A nested scope must not swallow the correlation it was opened inside.");
        }
    }

    [TestMethod]
    public void Ensure_AdoptsAnExistingIdRatherThanReplacingIt()
    {
        // The distinction Ensure/Begin encodes: work CAUSED by something upstream keeps that
        // cause. Replacing it here would silently split one trace into two.
        using (CorrelationContext.Begin("caller"))
            Assert.AreEqual("caller", CorrelationContext.Ensure());
    }

    [TestMethod]
    public async Task Ensure_MintsForBackgroundWorkNothingUpstreamCaused()
    {
        // Run detached so no ambient scope leaks in from the test's own context.
        var id = await Task.Run(() =>
        {
            var minted = CorrelationContext.Ensure();
            Assert.AreEqual(minted, CorrelationContext.Current);
            return minted;
        });

        Assert.IsFalse(string.IsNullOrWhiteSpace(id));
    }

    [TestMethod]
    public async Task CorrelationFlowsAcrossAwait()
    {
        using (CorrelationContext.Begin("flows"))
        {
            await Task.Yield();
            await Task.Delay(1);
            Assert.AreEqual("flows", CorrelationContext.Current);
        }
    }

    [TestMethod]
    public async Task CorrelationDoesNotFlowAcrossAQueueHandoff_WhichIsWhyICommandCarriesIt()
    {
        // The failure this whole mechanism exists for, reproduced. A consumer loop that started
        // before the producer never captured the producer's execution context, so the ambient is
        // simply not there on the far side — no error, just a null.
        var handoff = new TaskCompletionSource<string?>();
        var consumerReady = new TaskCompletionSource();

        // A "lane loop": running before any correlation is established, as the real one is.
        var consumer = Task.Run(async () =>
        {
            consumerReady.SetResult();
            var command = await _queued.Task;
            handoff.SetResult(CorrelationContext.Current);
            return command;
        });

        await consumerReady.Task;

        ICommand produced;
        using (CorrelationContext.Begin("producer-id"))
        {
            produced = new AgentCommand { SessionKey = "s", Message = "t" };
            Assert.AreEqual("producer-id", produced.CorrelationId,
                "A command must capture the ambient id at CONSTRUCTION, while still inside the cause.");
            _queued.SetResult(produced);
        }

        await consumer;
        Assert.IsNull(await handoff.Task,
            "If this ever starts passing an id, the queue hop propagates on its own and "
            + "CommandProcessor's re-entry is no longer load-bearing — verify before deleting it.");

        // And the re-entry that repairs it, which is what CommandProcessor.ExecuteHandlerAsync does.
        using (CorrelationContext.Begin(produced.CorrelationId))
            Assert.AreEqual("producer-id", CorrelationContext.Current);
    }

    private readonly TaskCompletionSource<ICommand> _queued = new();

    [TestMethod]
    public void SpansCarryTheAmbientCorrelationId()
    {
        using var listener = ListenTo(AgentTelemetry.AgentSourceName);

        using (CorrelationContext.Begin("stamped"))
        {
            using var span = AgentTelemetry.Start(AgentTelemetry.Agent, "test.span");
            Assert.IsNotNull(span, "The listener above should make this source sampled.");
            Assert.AreEqual("stamped", span.GetTagItem(AgentTelemetry.CorrelationIdAttribute));
        }
    }

    [TestMethod]
    public void ARefusalIsRecordedAsAnError_NotAsASuccessfulSpan()
    {
        using var listener = ListenTo(AgentTelemetry.AgentSourceName);
        using var span = AgentTelemetry.Start(AgentTelemetry.Agent, "test.refusal");

        AgentTelemetry.SetOutcome(span, succeeded: false, "gate said no");

        Assert.AreEqual(ActivityStatusCode.Error, span!.Status);
        Assert.AreEqual("refused", span.GetTagItem("agentfox.outcome"));
        Assert.AreEqual("gate said no", span.GetTagItem("agentfox.reason"));
    }

    [TestMethod]
    public void InstrumentationHelpersAreNullSafeWhenNothingIsListening()
    {
        // No listener: StartActivity returns null by design, and every call site uses `?.`.
        // Behaviour must be identical whether telemetry is on or off.
        var span = AgentTelemetry.Start(AgentTelemetry.Agent, "unlistened");
        Assert.IsNull(span);

        AgentTelemetry.SetOutcome(span, succeeded: true);
        AgentTelemetry.SetOutcome(span, succeeded: false, "reason");
        AgentTelemetry.SetError(span, new InvalidOperationException("boom"));
    }

    private static ActivityListener ListenTo(string sourceName)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
