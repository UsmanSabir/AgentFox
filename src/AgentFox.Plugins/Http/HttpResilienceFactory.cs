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
            // Outermost on purpose: this is a TOTAL operation budget, including retries and their
            // backoff delays. Putting timeout after retry gives every attempt a fresh full budget,
            // so a nominal 25s request can occupy its caller for well over a minute.
            .AddTimeout(totalTimeout ?? TimeSpan.FromSeconds(60))
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
                // Authenticate to an authenticating corporate proxy using the process's own Windows
                // identity, the way every browser on such a network already does.
                //
                // .NET picks up the system proxy automatically but sends no credentials for it, so on
                // a network with one every outbound call dies at the CONNECT tunnel with 407 — long
                // before it reaches the host it was aimed at. The symptom is a plugin that looks
                // simply broken: on a NETSOL workstation on 2026-08-18 this silently emptied PSX
                // candle history, one "market day could not be loaded" warning per day requested,
                // with nothing pointing at a proxy. Harmless where no proxy exists, and ignored by a
                // proxy that wants no authentication.
                DefaultProxyCredentials = CredentialCache.DefaultCredentials,
            }
        };

        return new HttpClient(handler);
    }
}
