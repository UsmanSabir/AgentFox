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
/// A persistent order that expires says so, through <see cref="IPersistentOrderObserver"/>.
///
/// <para>
/// Expiry is the one way a keep-working order ends that no broker event reports, and until this seam
/// existed it wrote its own state and nothing else — not even the activity row a fulfilled intent gets.
/// These drive a real lifecycle pass against a real repository, because the property worth pinning is
/// that the worker CALLS the observer, and with the figures the ledger actually holds.
/// </para>
/// </summary>
[TestClass]
public sealed class PersistentOrderExpiryNotificationTests
{
    private string _root = "";

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "agentfox-expiry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private sealed class RecordingObserver : IPersistentOrderObserver
    {
        public List<(PersistentOrderIntent Intent, string Detail)> Expired { get; } = [];
        public Exception? Throws { get; init; }

        public Task ExpiredAsync(PersistentOrderIntent intent, string detail, CancellationToken ct = default)
        {
            if (Throws is not null) throw Throws;
            Expired.Add((intent, detail));
            return Task.CompletedTask;
        }
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

    /// <summary>
    /// Only what an expiry with nothing resting touches. Everything else is null on purpose: if the
    /// expiry branch ever starts reaching for the broker or the order path, these tests say so.
    /// </summary>
    private static PersistentOrderWorker Worker(
        ITradingRepository repository, TradingActivityLog activity, params IPersistentOrderObserver[] observers)
    {
        var reconciliation = new TradingReconciliationState();
        reconciliation.Update(new BrokerReconciliationSnapshot(true, true, "ok", DateTime.UtcNow));

        return new PersistentOrderWorker(
            repository, null!, null!, null!, new OpenCalendar(), reconciliation, null!, null!, null!, null!,
            Options.Create(new TradingAgentOptions()), NullLogger<PersistentOrderWorker>.Instance,
            activity, observers);
    }

    private static async Task<string> SaveExpiredAsync(
        ITradingRepository repository, string symbol, int quantity, int filled)
    {
        var intent = new PersistentOrderIntent
        {
            IntentId = Guid.NewGuid().ToString("N"),
            Symbol = symbol,
            Action = "BUY",
            Quantity = quantity,
            OrderType = "LIMIT",
            Price = 95.53m,
            FilledQuantity = filled,
            State = filled > 0 ? "partial" : "active",
            ExpiresUtc = DateTime.UtcNow.AddMinutes(-1),
            CreatedUtc = DateTime.UtcNow.AddDays(-3),
            UpdatedUtc = DateTime.UtcNow
        };
        await repository.SavePersistentOrderAsync(intent);
        return intent.IntentId;
    }

    [TestMethod]
    public async Task AnExpiredIntentIsAnnouncedWithWhatItLeftUnfilled()
    {
        var repository = NewRepository();
        var activity = new TradingActivityLog();
        var observer = new RecordingObserver();
        var id = await SaveExpiredAsync(repository, "MWMP", quantity: 100, filled: 40);

        await Worker(repository, activity, observer).RunNowAsync();

        var (intent, detail) = observer.Expired.Single();
        Assert.AreEqual(id, intent.IntentId);
        Assert.AreEqual("expired", intent.State, "observers are told after the write, never before it");
        Assert.AreEqual(40, intent.FilledQuantity);
        Assert.AreEqual(60, intent.RemainingQuantity);
        StringAssert.Contains(detail, "60 share(s) unfilled");

        Assert.AreEqual("expired", (await repository.GetPersistentOrderAsync(id))!.State);
    }

    [TestMethod]
    public async Task AnExpiryIsAnnouncedOnceNotOnEveryPass()
    {
        // An expired intent is terminal and drops out of the open set, so the next pass never sees it.
        // Pinned because a repeating expiry is how a channel becomes noise the operator mutes.
        var repository = NewRepository();
        var observer = new RecordingObserver();
        await SaveExpiredAsync(repository, "MWMP", quantity: 100, filled: 0);
        var worker = Worker(repository, new TradingActivityLog(), observer);

        await worker.RunNowAsync();
        await worker.RunNowAsync();
        await worker.RunNowAsync();

        Assert.AreEqual(1, observer.Expired.Count);
    }

    [TestMethod]
    public async Task AnExpiryAlsoWritesAnActivityRow()
    {
        // A fulfilled intent always wrote one and an expired one did not, so the activity log — where
        // an operator looks to find out why nothing happened — was silent on exactly that case.
        var repository = NewRepository();
        var activity = new TradingActivityLog();
        await SaveExpiredAsync(repository, "MWMP", quantity: 100, filled: 0);

        await Worker(repository, activity).RunNowAsync();

        var row = activity.Snapshot().Single(e => e.Message.Contains("expired"));
        StringAssert.Contains(row.Message, "MWMP");
        StringAssert.Contains(row.Detail!, "100 unfilled");
    }

    [TestMethod]
    public async Task AnObserverThatThrowsCannotStopTheExpiryOrTheNextIntent()
    {
        // The pass holds the run gate and walks every intent. One edition's formatter throwing must not
        // leave this intent unexpired, nor abort the intents after it.
        var repository = NewRepository();
        var broken = new RecordingObserver { Throws = new InvalidOperationException("channel is down") };
        var working = new RecordingObserver();
        var first = await SaveExpiredAsync(repository, "MWMP", quantity: 100, filled: 0);
        var second = await SaveExpiredAsync(repository, "LUCK", quantity: 35, filled: 0);

        await Worker(repository, new TradingActivityLog(), broken, working).RunNowAsync();

        Assert.AreEqual("expired", (await repository.GetPersistentOrderAsync(first))!.State);
        Assert.AreEqual("expired", (await repository.GetPersistentOrderAsync(second))!.State);
        Assert.AreEqual(2, working.Expired.Count,
            "an observer registered after a broken one must still hear about both intents");
    }
}
