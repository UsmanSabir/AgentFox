#pragma warning disable EXTEXP0001
using Microsoft.Extensions.Http.Resilience;
using Polly;
using System.Net;

namespace AgentFox.Http;

/// <summary>
/// Shared resilient HttpClient factory for first-party and external plugins.
/// </summary>
public static class HttpResilienceFactory
{
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
            .Build();

        var client = BuildClient(pipeline);
        client.Timeout = clientTimeout;
        return client;
    }

    public static HttpClient CreateForHealthCheck(TimeSpan timeout)
    {
        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = 1,
                Delay = TimeSpan.FromMilliseconds(500),
                BackoffType = DelayBackoffType.Constant,
                UseJitter = false,
                ShouldHandle = args =>
                {
                    if (args.Outcome.Result is { } resp &&
                        (resp.StatusCode == HttpStatusCode.Unauthorized ||
                         resp.StatusCode == HttpStatusCode.Forbidden))
                    {
                        return ValueTask.FromResult(false);
                    }

                    return new HttpRetryStrategyOptions().ShouldHandle(args);
                }
            })
            .AddTimeout(timeout)
            .Build();

        var client = BuildClient(pipeline);
        client.Timeout = Timeout.InfiniteTimeSpan;
        return client;
    }

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
