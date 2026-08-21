namespace TradingAgent.Feed;

/// <summary>Pure retry arithmetic shared by the session client and its tests.</summary>
internal static class AhkSessionRetryPolicy
{
    public static TimeSpan LoginBackoff(int consecutiveFailures, int initialSeconds, int maxSeconds)
    {
        var initial = Math.Max(15, initialSeconds);
        var maximum = Math.Max(initial, maxSeconds);
        var exponent = Math.Clamp(consecutiveFailures - 1, 0, 10);
        return TimeSpan.FromSeconds(Math.Min(maximum, initial * Math.Pow(2, exponent)));
    }

    public static TimeSpan KeepAliveBackoff(int consecutiveFailures, int normalSeconds, int maxSeconds)
    {
        var normal = Math.Max(15, normalSeconds);
        var maximum = Math.Max(normal, maxSeconds);
        var exponent = Math.Clamp(consecutiveFailures - 1, 0, 6);
        return TimeSpan.FromSeconds(Math.Min(maximum, normal * Math.Pow(2, exponent)));
    }

    public static DateTime NextLoginAttemptUtc(
        DateTime failureUtc,
        DateTime lastAttemptUtc,
        int consecutiveFailures,
        int initialSeconds,
        int maxSeconds,
        int minimumLoginIntervalSeconds)
    {
        var afterBackoff = failureUtc + LoginBackoff(consecutiveFailures, initialSeconds, maxSeconds);
        var afterBrokerInterval = lastAttemptUtc.AddSeconds(Math.Max(30, minimumLoginIntervalSeconds));
        return afterBackoff > afterBrokerInterval ? afterBackoff : afterBrokerInterval;
    }
}
