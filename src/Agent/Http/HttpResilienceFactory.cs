#pragma warning disable EXTEXP0001 // ResilienceHandler is experimental but stable enough for production use
using Microsoft.Extensions.Http.Resilience;
using Polly;
using System.Net;

namespace AgentFox.Http;

/// <summary>
/// Centralized factory for creating resilient <see cref="HttpClient"/> instances using
/// Microsoft.Extensions.Http.Resilience (Polly v8).
///
/// All <see cref="HttpClient"/> instances in AgentFox should be created via this factory
/// instead of <c>new HttpClient()</c> so that network calls are automatically retried
/// on transient failures (connection errors, timeouts, 5xx, 429).
/// </summary>
internal static class HttpResilienceFactory
{
    /// <summary>
    /// Creates a fully resilient <see cref="HttpClient"/> with:
    /// <list type="bullet">
    ///   <item>Retry: up to 3 attempts, exponential back-off starting at 1 s, jitter</item>
    ///   <item>Circuit-breaker: opens after 50 % failure over 30 s (min 5 requests), breaks for 15 s</item>
    ///   <item>Total timeout: <paramref name="totalTimeout"/> (default 60 s) across all attempts</item>
    /// </list>
    /// Suitable for: MCP servers, Composio API, SkillHttpHelper, FetchUrl.
    /// </summary>
    public static HttpClient Create(TimeSpan? totalTimeout = null)
    {
        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(1),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
            })
            .AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                SamplingDuration = TimeSpan.FromSeconds(30),
                MinimumThroughput = 5,
                FailureRatio = 0.5,
                BreakDuration = TimeSpan.FromSeconds(15),
            })
            .AddTimeout(totalTimeout ?? TimeSpan.FromSeconds(60))
            .Build();

        return BuildClient(pipeline);
    }

    /// <summary>
    /// Creates a resilient <see cref="HttpClient"/> for long-polling loops (e.g. Telegram).
    /// No circuit-breaker (polling errors are transient, not service-wide failures).
    /// The caller is responsible for setting <see cref="HttpClient.Timeout"/> after creation
    /// if a per-request timeout is needed.
    /// </summary>
    public static HttpClient CreateForPolling(TimeSpan clientTimeout)
    {
        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = 5,
                Delay = TimeSpan.FromSeconds(2),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
            })
            // No pipeline-level timeout: HttpClient.Timeout controls the per-request window.
            .Build();

        var client = BuildClient(pipeline);
        client.Timeout = clientTimeout;
        return client;
    }

    /// <summary>
    /// Creates a resilient <see cref="HttpClient"/> for one-shot diagnostic / health-check calls.
    /// Single retry, no circuit-breaker, short timeout.
    /// Auth errors (401/403) are never retried.
    /// </summary>
    public static HttpClient CreateForHealthCheck(TimeSpan timeout)
    {
        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = 1,
                Delay = TimeSpan.FromMilliseconds(500),
                BackoffType = DelayBackoffType.Constant,
                UseJitter = false,
                // Skip retry for auth failures — they won't succeed on retry.
                ShouldHandle = args =>
                {
                    if (args.Outcome.Result is { } resp &&
                        (resp.StatusCode == HttpStatusCode.Unauthorized ||
                         resp.StatusCode == HttpStatusCode.Forbidden))
                        return ValueTask.FromResult(false);

                    return new HttpRetryStrategyOptions().ShouldHandle(args);
                }
            })
            .AddTimeout(timeout)
            .Build();

        var client = BuildClient(pipeline);
        client.Timeout = Timeout.InfiniteTimeSpan; // pipeline timeout governs
        return client;
    }

    // ── internals ──────────────────────────────────────────────────────────────

    private static HttpClient BuildClient(ResiliencePipeline<HttpResponseMessage> pipeline)
    {
        var handler = new ResilienceHandler(pipeline)
        {
            InnerHandler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                KeepAlivePingDelay = TimeSpan.FromSeconds(30),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
            }
        };
        return new HttpClient(handler);
    }
}
