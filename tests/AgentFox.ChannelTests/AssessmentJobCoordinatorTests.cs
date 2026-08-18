using Microsoft.Extensions.Logging.Abstractions;
using TradingAgent.Research;

namespace AgentFox.ChannelTests;

[TestClass]
public sealed class AssessmentJobCoordinatorTests
{
    [TestMethod]
    public async Task SubmittedWork_CompletesWithoutARequestCancellationToken()
    {
        var coordinator = Coordinator();
        await coordinator.StartAsync(CancellationToken.None);
        try
        {
            using var request = new CancellationTokenSource();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var submission = coordinator.Submit("MARI|1D", async workerToken =>
            {
                Assert.IsFalse(request.Token.Equals(workerToken),
                    "Background work must use the application lifetime, not RequestAborted.");
                await release.Task.WaitAsync(workerToken);
                return new { assessment = "done" };
            });

            request.Cancel();
            release.SetResult();

            var completed = await WaitForTerminalAsync(coordinator, submission.JobId);
            Assert.AreEqual("succeeded", completed.State);
            Assert.IsNotNull(completed.Result);
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
            coordinator.Dispose();
        }
    }

    [TestMethod]
    public async Task IdenticalActiveRequests_ReuseOneJob_ThenAllowANewSubmission()
    {
        var coordinator = Coordinator();
        await coordinator.StartAsync(CancellationToken.None);
        try
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            Task<object> Work(CancellationToken ct)
            {
                Interlocked.Increment(ref calls);
                return FinishAsync(ct);
            }

            async Task<object> FinishAsync(CancellationToken ct)
            {
                await release.Task.WaitAsync(ct);
                return "ok";
            }

            var first = coordinator.Submit("OGDC|1D", Work);
            var duplicate = coordinator.Submit("OGDC|1D", Work);
            Assert.AreEqual(first.JobId, duplicate.JobId);
            Assert.IsTrue(duplicate.Reused);

            release.SetResult();
            await WaitForTerminalAsync(coordinator, first.JobId);
            Assert.AreEqual(1, calls);

            var retry = coordinator.Submit("OGDC|1D", _ => Task.FromResult<object>("again"));
            Assert.AreNotEqual(first.JobId, retry.JobId,
                "Completed job retention must not suppress an explicit retry; the assessment cache owns reuse.");
            await WaitForTerminalAsync(coordinator, retry.JobId);
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
            coordinator.Dispose();
        }
    }

    [TestMethod]
    public async Task FailedWork_IsReportedWithoutKillingTheWorker()
    {
        var coordinator = Coordinator();
        await coordinator.StartAsync(CancellationToken.None);
        try
        {
            var failed = coordinator.Submit("bad", _ => throw new InvalidOperationException("model down"));
            var failure = await WaitForTerminalAsync(coordinator, failed.JobId);
            Assert.AreEqual("failed", failure.State);
            Assert.AreEqual("model down", failure.Error);

            var next = coordinator.Submit("good", _ => Task.FromResult<object>(42));
            Assert.AreEqual("succeeded", (await WaitForTerminalAsync(coordinator, next.JobId)).State);
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
            coordinator.Dispose();
        }
    }

    private static AssessmentJobCoordinator Coordinator() =>
        new(NullLogger<AssessmentJobCoordinator>.Instance);

    private static async Task<AssessmentJobSnapshot> WaitForTerminalAsync(
        AssessmentJobCoordinator coordinator, string jobId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = coordinator.Get(jobId);
            if (snapshot?.State is "succeeded" or "failed") return snapshot;
            await Task.Delay(10);
        }

        Assert.Fail($"Assessment job {jobId} did not finish.");
        throw new InvalidOperationException();
    }
}
