using AgentFox.Agents;
using AgentFox.Plugins.Models;

namespace AgentFox.ChannelTests;

[TestClass]
public sealed class WebChatTurnCoordinatorTests
{
    [TestMethod]
    public async Task TurnsAreSerializedPerConversation()
    {
        var coordinator = new WebChatTurnCoordinator();
        var firstStarted = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var first = coordinator.Enqueue("conversation", async (_, _) =>
        {
            firstStarted.TrySetResult(null);
            await releaseFirst.Task;
            return WebChatTurnResult.Completed(new AgentReply { Output = "first" });
        });
        first.Release();
        await firstStarted.Task;

        var second = coordinator.Enqueue("conversation", async (_, _) =>
        {
            secondStarted.TrySetResult(null);
            return WebChatTurnResult.Completed(new AgentReply { Output = "second" });
        });
        second.Release();

        Assert.IsFalse(secondStarted.Task.IsCompleted);
        releaseFirst.TrySetResult(null);

        Assert.AreEqual(WebChatTurnState.Completed, (await first.Completion).State);
        await secondStarted.Task;
        Assert.AreEqual(WebChatTurnState.Completed, (await second.Completion).State);
    }

    [TestMethod]
    public async Task SteerCancelsActiveTurnAndPromotesSelectedQueuedTurn()
    {
        var coordinator = new WebChatTurnCoordinator();
        var firstStarted = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var first = coordinator.Enqueue("conversation", async (_, ct) =>
        {
            firstStarted.TrySetResult(null);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return WebChatTurnResult.Completed(new AgentReply { Output = "unexpected" });
        });
        first.Release();
        await firstStarted.Task;

        var second = coordinator.Enqueue("conversation", async (_, _) =>
        {
            secondStarted.TrySetResult(null);
            return WebChatTurnResult.Completed(new AgentReply { Output = "steered" });
        });
        second.Release();

        Assert.IsTrue(coordinator.Steer("conversation", second.RunId));
        Assert.AreEqual(WebChatTurnState.Interrupted, (await first.Completion).State);
        await secondStarted.Task;
        Assert.AreEqual(WebChatTurnState.Completed, (await second.Completion).State);
    }

    [TestMethod]
    public async Task DifferentConversationsCanRunInParallel()
    {
        var coordinator = new WebChatTurnCoordinator();
        var firstStarted = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var first = coordinator.Enqueue("one", async (_, _) =>
        {
            firstStarted.TrySetResult(null);
            await release.Task;
            return WebChatTurnResult.Completed(new AgentReply { Output = "one" });
        });
        var second = coordinator.Enqueue("two", async (_, _) =>
        {
            secondStarted.TrySetResult(null);
            await release.Task;
            return WebChatTurnResult.Completed(new AgentReply { Output = "two" });
        });
        first.Release();
        second.Release();

        await Task.WhenAll(firstStarted.Task, secondStarted.Task);
        release.TrySetResult(null);
        await Task.WhenAll(first.Completion, second.Completion);
    }
}
