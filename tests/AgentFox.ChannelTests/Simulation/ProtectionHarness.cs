using AgentFox.Plugins;
using AgentFox.Plugins.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Manager;
using TradingAgent.Market;
using TradingAgent.Models;
using TradingAgent.Persistence;
using TradingAgent.Reconciliation;
using TradingAgent.Risk;
using TradingAgent.Trading;
using TradingAgent.Watchlist;

namespace AgentFox.ChannelTests.Simulation;

/// <summary>
/// The REAL protective-stop pipeline — <see cref="ProtectiveStopWorker"/>, <see cref="TradingManager"/>,
/// <see cref="ApprovalGate"/>, the risk engine, the order window and a SQLite ledger on a temp file —
/// wired to a <see cref="SimulatedBroker"/>, so a scenario exercises every gate a live stop passes
/// through and only the venue is fake.
///
/// <para>
/// The policy is the hosted product's live one, measured on the VM 2026-11-28: BoundedAuto with
/// AutoExecute on and the reconciliation gate off. A scenario that needs another posture changes
/// <see cref="Options"/> or <see cref="Ahk"/> before its first pass.
/// </para>
/// </summary>
internal sealed class ProtectionHarness : IDisposable
{
    private readonly string _root;
    private readonly IServiceScopeFactory _scopes;
    private readonly ApprovalGate _approvals;
    private readonly OrderWindow _window;
    private readonly TradingPolicyProvider _policy;
    private readonly FixedAhk _liveAhk;

    public SimulatedBroker Broker { get; } = new();
    public SimCalendar Calendar { get; } = new();
    public RecordingNotifier Notifier { get; } = new();
    public ListLogger<ProtectiveStopWorker> Log { get; } = new();
    public TradingAgentOptions Options { get; }
    public AhkConfig Ahk { get; }
    public SqliteTradingRepository Repository { get; }
    public TradingManager Manager { get; }
    public ProtectiveStopWorker Worker { get; private set; }

    public ProtectionHarness(decimal maxOrderValuePkr, params string[] symbols)
    {
        _root = Path.Combine(Path.GetTempPath(), "agentfox-protection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        Options = new TradingAgentOptions
        {
            AutoExecute = true,
            ExecutionMode = "BoundedAuto",
            RequireReconciliationHealthy = false,
            DatabasePath = Path.Combine(_root, "trading.db"),
            AllowedSymbols = symbols.ToList()
        };
        Ahk = new AhkConfig { MaxOrderValuePkr = maxOrderValuePkr };
        _liveAhk = new FixedAhk(Ahk);

        var options = Microsoft.Extensions.Options.Options.Create(Options);
        Repository = new SqliteTradingRepository(options, new ConfigurationBuilder().Build(),
            NullLogger<SqliteTradingRepository>.Instance);

        var services = new ServiceCollection();
        services.AddSingleton<ITradingRepository>(Repository);
        _scopes = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        _policy = new TradingPolicyProvider(options,
            new PluginConfigManager(Path.Combine(_root, "config"), NullLogger<PluginConfigManager>.Instance));
        // The broker's own market state, not the calendar: a halt reaches the order window exactly the
        // way a TradeCast HALT/SUS reading does.
        _window = new OrderWindow(Calendar, Broker, _liveAhk, NullLogger<OrderWindow>.Instance);
        var intents = new ApprovalIntentRegistry();
        _approvals = new ApprovalGate(intents, _policy, Calendar, _window, options,
            NullLogger<ApprovalGate>.Instance);

        Manager = new TradingManager(Broker, Repository, Calendar, _window, _policy,
            new TradingRiskEngine(Microsoft.Extensions.Options.Options.Create(Ahk), options, _liveAhk),
            new TradingReconciliationState(), intents, options, NullLogger<TradingManager>.Instance,
            brokerStateReader: Broker);

        Worker = NewWorker();
    }

    public DateOnly Today => DateOnly.FromDateTime(Calendar.GetStatus().PktNow);

    /// <summary>One periodic pass, exactly as the hosted worker's timer runs it.</summary>
    public Task RunPassAsync() => Worker.RunNowAsync();

    /// <summary>
    /// A process restart: a fresh worker over the same ledger and broker. Everything the worker keeps in
    /// memory — refusal backoffs, alert dedupe, stranded-order sightings — is lost, as it is in production.
    /// </summary>
    public void Restart() => Worker = NewWorker();

    /// <summary>
    /// A holding the broker already has, under an ACTIVE protective stop with no parent entry — the shape
    /// a stop attached to an existing position takes, which is placed on the very next pass.
    /// </summary>
    public async Task<ProtectiveStop> ProtectHoldingAsync(
        string symbol, int quantity, decimal price, decimal trigger, decimal limit)
    {
        Broker.SetHolding(symbol, quantity, price);
        var stop = new ProtectiveStop
        {
            StopId = Guid.NewGuid().ToString("N"),
            Symbol = symbol,
            StopTrigger = trigger,
            StopLimit = limit,
            DesiredQuantity = quantity,
            State = "active",
            OperatorOriginated = true
        };
        await Repository.SaveProtectiveStopAsync(stop);
        return stop;
    }

    public async Task<ProtectiveStop> ReloadAsync(string stopId) =>
        (await Repository.GetProtectiveStopsAsync(openOnly: false)).Single(s => s.StopId == stopId);

    /// <summary>The close, then the next trading session's open, on both the venue and the calendar.</summary>
    public void RollToNextSession()
    {
        Broker.EndOfDay();
        Calendar.DayOffset++;
        Broker.NextSession();
    }

    /// <summary>
    /// Submits a protective stop's native order the way the worker does — same signal, same idempotency
    /// key, same approval — so a test can see what the execution boundary does with a same-day re-place.
    /// Mirrors <c>ProtectiveStopWorker.PlaceNativeStopAsync</c>; keep the two in step.
    /// </summary>
    public Task<TradingExecutionResult> SubmitAsWorkerWouldAsync(ProtectiveStop stop, int quantity)
    {
        var signal = new TradingSignal
        {
            IsSignal = true,
            Action = "SELL",
            Symbol = stop.Symbol,
            Quantity = quantity,
            OrderType = "STOPLOSS",
            EntryPrice = stop.StopTrigger,
            LimitPrice = stop.StopLimit,
            Confidence = "HIGH",
            RawMessage = $"protective-stop:{stop.StopId}"
        };
        var groups = new[] { (IReadOnlyList<TradingSignal>)[signal] };
        var key = $"protective-stop:{stop.StopId}:{Today:yyyyMMdd}:{quantity}";
        var approval = _approvals.Decide(groups, key,
            new ApprovalContext(null, "protective-stop", stop.OperatorOriginated));
        return Manager.ExecuteGroupsAsync(groups, key, approval.Authorization);
    }

    private ProtectiveStopWorker NewWorker() => new(
        _scopes, Broker, Broker, Manager, _approvals, Calendar, _window, _policy,
        Microsoft.Extensions.Options.Options.Create(Options), Log,
        activity: null, notifier: Notifier, replacementObservers: null, brokerConfig: _liveAhk);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a pooled handle outlived the test; the temp folder is harmless */ }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class FixedAhk(AhkConfig value) : IRuntimePluginOptions<AhkConfig>
    {
        public AhkConfig Current => value;
    }
}

/// <summary>
/// A calendar that is always inside a session unless told otherwise, on today's real clock moved forward
/// by <see cref="DayOffset"/> whole sessions — so "the next session" is a real date change for every
/// rule keyed on the PKT date (session coverage, alert dedupe, idempotency keys).
/// </summary>
internal sealed class SimCalendar : IMarketCalendar
{
    public bool IsOpen { get; set; } = true;
    public int DayOffset { get; set; }

    public MarketStatus GetStatus(DateTime? utcNow = null)
    {
        var pktNow = (utcNow ?? DateTime.UtcNow).AddHours(5).AddDays(DayOffset);
        return new MarketStatus(IsOpen, pktNow, IsOpen ? "simulated session" : "simulated close",
            SessionOpenPkt: IsOpen ? pktNow.Date.AddHours(9).AddMinutes(32) : null);
    }

    public bool IsTradingDay(DateOnly date) => true;
}

/// <summary>Records every alert with its routing topic instead of delivering it.</summary>
internal sealed class RecordingNotifier : IUserNotifier
{
    private readonly List<(string Message, string? Topic)> _sent = [];

    public IReadOnlyList<(string Message, string? Topic)> Sent { get { lock (_sent) return _sent.ToList(); } }

    public Task<int> NotifyAsync(string message, CancellationToken ct = default) =>
        NotifyAsync(message, null, ct);

    public Task<int> NotifyAsync(string message, string? topic, CancellationToken ct = default)
    {
        lock (_sent) _sent.Add((message, topic));
        return Task.FromResult(1);
    }
}

/// <summary>Captures every log line at every level, so a test can assert WHY the worker did nothing.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines { get { lock (_lines) return _lines.ToList(); } }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_lines) _lines.Add($"{logLevel}: {formatter(state, exception)}");
    }
}
