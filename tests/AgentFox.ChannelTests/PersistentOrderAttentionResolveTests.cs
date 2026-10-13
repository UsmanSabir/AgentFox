using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Market;
using TradingAgent.Observability;
using TradingAgent.Persistence;
using TradingAgent.Reconciliation;
using TradingAgent.Trading;

namespace AgentFox.ChannelTests;

/// <summary>
/// Resolving an "attention" intent must settle the PLACEMENT, not only the intent.
///
/// <para>
/// MLCF, 2026-09-28: a BUY placed on 2026-09-25 was never observed at that day's close, so the next
/// pass stopped at attention. Resolving it — by the operator's button and by the custody check — wrote
/// the intent back to <c>active</c> and left the placement <c>accepted</c>. The following pass asked
/// <see cref="PersistentOrderDecisions.PriorOutcomeWasNotObserved"/> again, got the same yes from the
/// same row, and wrote attention again. Resolved twice, never re-placed.
/// </para>
/// </summary>
[TestClass]
public sealed class PersistentOrderAttentionResolveTests
{
    private static readonly DateOnly PriorDate = new(2026, 9, 25);
    private static readonly DateOnly Today = new(2026, 9, 28);

    private string _root = "";

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "agentfox-attention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private sealed class OpenCalendar : IMarketCalendar
    {
        public MarketStatus GetStatus(DateTime? utcNow = null) =>
            new(true, DateTime.UtcNow.AddHours(5), "open");
    }

    private SqliteTradingRepository NewRepository()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Workspaces:0"] = _root })
            .Build();

        return new SqliteTradingRepository(
            Options.Create(new TradingAgentOptions { DatabasePath = "trading/trading.db" }),
            configuration,
            NullLogger<SqliteTradingRepository>.Instance);
    }

    /// <summary>Resolution touches only the repository; everything else is null on purpose.</summary>
    private static PersistentOrderWorker Worker(ITradingRepository repository) =>
        new(repository, null!, null!, null!, new OpenCalendar(), new TradingReconciliationState(),
            null!, null!, null!, null!, Options.Create(new TradingAgentOptions()),
            NullLogger<PersistentOrderWorker>.Instance, new TradingActivityLog(), []);

    /// <summary>The MLCF shape: an accepted prior-date placement whose close was never observed.</summary>
    private static async Task<string> SaveUnobservedAsync(ITradingRepository repository)
    {
        var intent = new PersistentOrderIntent
        {
            IntentId = Guid.NewGuid().ToString("N"),
            Symbol = "MLCF",
            Action = "BUY",
            Quantity = 109,
            OrderType = "LIMIT",
            Price = 91m,
            State = "placing",
            LastAttemptSessionDate = PriorDate,
            AttemptCount = 2,
            ExpiresUtc = DateTime.UtcNow.AddDays(20),
            CreatedUtc = DateTime.UtcNow.AddDays(-5),
            UpdatedUtc = DateTime.UtcNow
        };
        await repository.SavePersistentOrderAsync(intent);
        await repository.RecordPersistentOrderPlacementAsync(new PersistentOrderPlacement
        {
            PlacementId = Guid.NewGuid().ToString("N"),
            IntentId = intent.IntentId,
            SessionDate = PriorDate,
            Attempt = 2,
            Quantity = 109,
            BrokerOrderNo = "5434",
            State = "accepted"
        }, "attention", "end-of-day outcome was not observed");
        return intent.IntentId;
    }

    [DataTestMethod]
    [DataRow("not_filled", null, "active")]
    [DataRow("partial", 40, "partial")]
    public async Task AResolvedIntentDoesNotFallBackIntoAttention(
        string resolution, int? filled, string expectedState)
    {
        var repository = NewRepository();
        var id = await SaveUnobservedAsync(repository);

        var result = await Worker(repository).ResolveAttentionAsync(
            id, resolution, filled, "checked the broker app", "operator");

        Assert.IsTrue(result.Applied, result.Message);
        var intent = (await repository.GetPersistentOrderAsync(id))!;
        var latest = (await repository.GetPersistentOrderPlacementsAsync(id)).Last();
        Assert.AreEqual(expectedState, intent.State);
        Assert.AreEqual("lapsed", latest.State,
            "an 'accepted' placement left behind is what re-raised attention on the next pass");
        Assert.IsFalse(PersistentOrderDecisions.PriorOutcomeWasNotObserved(intent, latest, Today));
        Assert.IsTrue(PersistentOrderDecisions.MayAttempt(intent, DateTime.UtcNow, Today, false, out var why), why);
    }

    [TestMethod]
    public async Task ARefusedResolutionLeavesThePlacementAlone()
    {
        // Nothing was settled, so the placement must still say "unobserved".
        var repository = NewRepository();
        var id = await SaveUnobservedAsync(repository);

        var result = await Worker(repository).ResolveAttentionAsync(id, "bogus", null, "", "operator");

        Assert.IsFalse(result.Applied);
        Assert.AreEqual("accepted", (await repository.GetPersistentOrderPlacementsAsync(id)).Last().State);
    }
}
