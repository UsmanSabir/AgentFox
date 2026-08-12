using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Persistence;

namespace AgentFox.ChannelTests;

/// <summary>
/// The proposal queue's state machine.
///
/// <para>
/// This table used to be write-only: rows were created, listed, and never resolved, so it only ever
/// grew and "pending proposals" only ever climbed. These tests pin the behaviours that make it a work
/// queue instead — above all the compare-and-set, which is what stops a double click from executing
/// the same orders twice.
/// </para>
/// </summary>
[TestClass]
public sealed class ProposalLifecycleTests
{
    [TestMethod]
    public async Task ANewProposal_IsOpenAndActionable()
    {
        using var env = Env.Create();
        var id = await env.Repository.CreateProposalAsync("key-1", Proposal(), "v1");

        var open = await env.Repository.GetOpenProposalsAsync();

        Assert.AreEqual(1, open.Count);
        Assert.AreEqual(id, open[0].ProposalId);
        Assert.IsNull(open[0].ExecutionId);
    }

    [TestMethod]
    public async Task ExecutingThenExecuted_LinksTheExecutionAndLeavesTheQueue()
    {
        using var env = Env.Create();
        var id = await env.Repository.CreateProposalAsync("key-1", Proposal(), "v1");
        var status = (await env.Repository.GetProposalAsync(id))!.Status;

        Assert.IsTrue(await env.Repository.TrySetProposalStateAsync(id, status, "executing"));
        Assert.IsTrue(await env.Repository.TrySetProposalStateAsync(
            id, "executing", "executed", executionId: "exec-99"));

        var record = await env.Repository.GetProposalAsync(id);
        Assert.AreEqual("executed", record!.Status);
        Assert.AreEqual("exec-99", record.ExecutionId,
            "An executed proposal must point at the execution it became, or the audit trail breaks.");
        Assert.AreEqual(0, (await env.Repository.GetOpenProposalsAsync()).Count,
            "A resolved proposal must leave the actionable queue.");
    }

    [TestMethod]
    public async Task ASecondClaim_IsRefused()
    {
        using var env = Env.Create();
        var id = await env.Repository.CreateProposalAsync("key-1", Proposal(), "v1");
        var status = (await env.Repository.GetProposalAsync(id))!.Status;

        Assert.IsTrue(await env.Repository.TrySetProposalStateAsync(id, status, "executing"));
        Assert.IsFalse(await env.Repository.TrySetProposalStateAsync(id, status, "executing"),
            "The compare-and-set is what makes a double click safe: the loser must be refused rather "
            + "than submitting the same orders a second time.");
    }

    [TestMethod]
    public async Task RejectionRecordsItsReason()
    {
        using var env = Env.Create();
        var id = await env.Repository.CreateProposalAsync("key-1", Proposal(), "v1");
        var status = (await env.Repository.GetProposalAsync(id))!.Status;

        await env.Repository.TrySetProposalStateAsync(id, status, "rejected", "Entry looked chased.");

        var record = await env.Repository.GetProposalAsync(id);
        Assert.AreEqual("rejected", record!.Status);
        Assert.AreEqual("Entry looked chased.", record.StateReason,
            "A terminal state without a reason is unexplainable a week later.");
    }

    [TestMethod]
    public async Task AResolvedProposal_CannotBeMovedAgain()
    {
        using var env = Env.Create();
        var id = await env.Repository.CreateProposalAsync("key-1", Proposal(), "v1");
        var status = (await env.Repository.GetProposalAsync(id))!.Status;
        await env.Repository.TrySetProposalStateAsync(id, status, "rejected", "no");

        Assert.IsFalse(await env.Repository.TrySetProposalStateAsync(id, status, "executing"),
            "Executing an already-rejected proposal must be impossible.");
    }

    [TestMethod]
    public async Task Pruning_RemovesResolvedRowsAndKeepsOpenOnes()
    {
        using var env = Env.Create();
        var open = await env.Repository.CreateProposalAsync("key-open", Proposal(), "v1");
        var done = await env.Repository.CreateProposalAsync("key-done", Proposal(), "v1");
        var status = (await env.Repository.GetProposalAsync(done))!.Status;
        await env.Repository.TrySetProposalStateAsync(done, status, "expired", "aged out");

        // Everything resolved before "now" is prunable; the open row must survive regardless of age.
        var removed = await env.Repository.PruneProposalsAsync(DateTime.UtcNow.AddMinutes(1));

        Assert.AreEqual(1, removed);
        Assert.IsNull(await env.Repository.GetProposalAsync(done));
        Assert.IsNotNull(await env.Repository.GetProposalAsync(open),
            "Retention must never silently discard a proposal that is still actionable.");
    }

    [TestMethod]
    public async Task UnknownProposal_ReadsAsNullRatherThanThrowing()
    {
        using var env = Env.Create();
        Assert.IsNull(await env.Repository.GetProposalAsync("nope"));
        Assert.IsFalse(await env.Repository.TrySetProposalStateAsync("nope", "proposed", "executed"));
    }

    [TestMethod]
    public async Task TheLedgerStatus_StopsCountingResolvedProposalsAsPending()
    {
        using var env = Env.Create();
        var id = await env.Repository.CreateProposalAsync("key-1", Proposal(), "v1");
        var status = (await env.Repository.GetProposalAsync(id))!.Status;

        var before = await env.Repository.GetStatusAsync();
        Assert.AreEqual(1, before.PendingProposals);

        await env.Repository.TrySetProposalStateAsync(id, status, "executed", executionId: "e1");

        var after = await env.Repository.GetStatusAsync();
        Assert.AreEqual(0, after.PendingProposals,
            "The pending count only ever climbed before, because nothing could leave the queue.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string Proposal() => """
        {"orders":[{"action":"BUY","symbol":"OGDC","quantity":10,"entry_price":300.0}],
         "rationale":"At weekly-confirmed support."}
        """;

    private sealed class Env : IDisposable
    {
        public required SqliteTradingRepository Repository { get; init; }
        public required string TempPath { get; init; }

        public static Env Create()
        {
            var temp = Path.Combine(Path.GetTempPath(), $"agentfox-proposal-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temp);

            var options = Options.Create(new TradingAgentOptions
            {
                DatabasePath = Path.Combine(temp, "trading.db"),
                AllowedSymbols = ["OGDC"]
            });

            return new Env
            {
                Repository = new SqliteTradingRepository(
                    options, new ConfigurationBuilder().Build(),
                    NullLogger<SqliteTradingRepository>.Instance),
                TempPath = temp
            };
        }

        public void Dispose()
        {
            try { Directory.Delete(TempPath, recursive: true); } catch { /* temp dir */ }
        }
    }
}
