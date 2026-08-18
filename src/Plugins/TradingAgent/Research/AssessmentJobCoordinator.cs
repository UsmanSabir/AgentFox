using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TradingAgent.Research;

/// <summary>
/// Runs slow, model-backed assessments independently of the HTTP request that submitted them.
/// A browser refresh or proxy disconnect therefore cannot cancel generation that is already in flight.
/// </summary>
public sealed class AssessmentJobCoordinator : BackgroundService
{
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(15);
    private const int MaxRetainedJobs = 100;

    private readonly Channel<string> _queue = System.Threading.Channels.Channel.CreateBounded<string>(new BoundedChannelOptions(32)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly ConcurrentDictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _activeByKey = new(StringComparer.Ordinal);
    private readonly ILogger<AssessmentJobCoordinator> _logger;

    public AssessmentJobCoordinator(ILogger<AssessmentJobCoordinator> logger) => _logger = logger;

    /// <summary>
    /// Queues one assessment. Identical queued/running work shares a job, but completed work does not:
    /// the existing assessment cache decides whether the underlying market situation is still identical.
    /// </summary>
    public AssessmentJobSubmission Submit(
        string deduplicationKey,
        Func<CancellationToken, Task<object>> work)
    {
        Prune();

        while (_activeByKey.TryGetValue(deduplicationKey, out var existingId))
        {
            if (_jobs.TryGetValue(existingId, out var existing) && !existing.IsTerminal)
                return new AssessmentJobSubmission(existingId, Reused: true);

            _activeByKey.TryRemove(new KeyValuePair<string, string>(deduplicationKey, existingId));
        }

        var id = Guid.NewGuid().ToString("N");
        var job = new Job(id, deduplicationKey, work);
        if (!_jobs.TryAdd(id, job))
            throw new InvalidOperationException("Could not allocate an assessment job.");

        if (!_activeByKey.TryAdd(deduplicationKey, id))
        {
            _jobs.TryRemove(id, out _);
            return Submit(deduplicationKey, work);
        }

        if (!_queue.Writer.TryWrite(id))
        {
            _activeByKey.TryRemove(new KeyValuePair<string, string>(deduplicationKey, id));
            _jobs.TryRemove(id, out _);
            throw new AssessmentQueueFullException();
        }

        return new AssessmentJobSubmission(id, Reused: false);
    }

    public AssessmentJobSnapshot? Get(string id) =>
        _jobs.TryGetValue(id, out var job) ? job.Snapshot() : null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var id in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            if (!_jobs.TryGetValue(id, out var job)) continue;

            job.MarkRunning();
            try
            {
                var result = await job.Work(stoppingToken);
                job.MarkSucceeded(result);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                job.MarkFailed("The application stopped before the assessment completed.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AssessmentJob] Job {JobId} failed.", id);
                job.MarkFailed(ex.Message);
            }
            finally
            {
                _activeByKey.TryRemove(new KeyValuePair<string, string>(job.DeduplicationKey, id));
                Prune();
            }
        }
    }

    private void Prune()
    {
        var cutoff = DateTime.UtcNow - Retention;
        foreach (var (id, job) in _jobs)
        {
            if (job.IsTerminal && job.CompletedUtc is { } completed && completed < cutoff)
                _jobs.TryRemove(id, out _);
        }

        if (_jobs.Count <= MaxRetainedJobs) return;
        foreach (var job in _jobs.Values
                     .Where(j => j.IsTerminal)
                     .OrderBy(j => j.CompletedUtc)
                     .Take(_jobs.Count - MaxRetainedJobs))
        {
            _jobs.TryRemove(job.Id, out _);
        }
    }

    private sealed class Job
    {
        private readonly object _sync = new();
        private string _state = "queued";
        private object? _result;
        private string? _error;
        private DateTime? _startedUtc;

        public Job(string id, string deduplicationKey, Func<CancellationToken, Task<object>> work)
        {
            Id = id;
            DeduplicationKey = deduplicationKey;
            Work = work;
            CreatedUtc = DateTime.UtcNow;
        }

        public string Id { get; }
        public string DeduplicationKey { get; }
        public Func<CancellationToken, Task<object>> Work { get; }
        public DateTime CreatedUtc { get; }
        public DateTime? CompletedUtc { get; private set; }
        public bool IsTerminal => Volatile.Read(ref _state) is "succeeded" or "failed";

        public void MarkRunning()
        {
            lock (_sync)
            {
                _state = "running";
                _startedUtc = DateTime.UtcNow;
            }
        }

        public void MarkSucceeded(object result)
        {
            lock (_sync)
            {
                _result = result;
                _state = "succeeded";
                CompletedUtc = DateTime.UtcNow;
            }
        }

        public void MarkFailed(string error)
        {
            lock (_sync)
            {
                _error = error;
                _state = "failed";
                CompletedUtc = DateTime.UtcNow;
            }
        }

        public AssessmentJobSnapshot Snapshot()
        {
            lock (_sync)
            {
                return new AssessmentJobSnapshot(
                    Id, _state, CreatedUtc, _startedUtc, CompletedUtc, _result, _error);
            }
        }
    }
}

public sealed record AssessmentJobSubmission(string JobId, bool Reused);

public sealed record AssessmentJobSnapshot(
    string JobId,
    string State,
    DateTime CreatedUtc,
    DateTime? StartedUtc,
    DateTime? CompletedUtc,
    object? Result,
    string? Error);

public sealed class AssessmentQueueFullException : Exception
{
    public AssessmentQueueFullException()
        : base("The assessment queue is full. Try again after the current model work completes.") { }
}
