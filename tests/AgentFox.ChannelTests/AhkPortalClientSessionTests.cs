using TradingAgent.Feed;

namespace AgentFox.ChannelTests;

[TestClass]
public sealed class AhkPortalClientSessionTests
{
    [TestMethod]
    public void EmptyJsonString_IsTheZombieSessionBalanceSignature()
    {
        Assert.IsTrue(AhkPortalClient.IsZombieSessionBalanceResponse("\"\""));
        Assert.IsTrue(AhkPortalClient.IsZombieSessionBalanceResponse("  \"\"\r\n"));
    }

    [TestMethod]
    public void MissingOrMalformedBalance_DoesNotSpendAFreshLogin()
    {
        Assert.IsFalse(AhkPortalClient.IsZombieSessionBalanceResponse(null));
        Assert.IsFalse(AhkPortalClient.IsZombieSessionBalanceResponse(""));
        Assert.IsFalse(AhkPortalClient.IsZombieSessionBalanceResponse("   "));
        Assert.IsFalse(AhkPortalClient.IsZombieSessionBalanceResponse("<html>temporarily unavailable</html>"));
        Assert.IsFalse(AhkPortalClient.IsZombieSessionBalanceResponse("\"78141.0\""));
        Assert.IsFalse(AhkPortalClient.IsZombieSessionBalanceResponse("0"));
    }

    [TestMethod]
    public void ReloginResponse_RecognizesDocumentedShortStatusStrings()
    {
        Assert.AreEqual(AhkReloginResponse.Healthy, AhkPortalClient.ClassifyReloginResponse("0"));
        Assert.AreEqual(AhkReloginResponse.Healthy, AhkPortalClient.ClassifyReloginResponse(" \"0\"\r\n"));
        Assert.AreEqual(AhkReloginResponse.Healthy, AhkPortalClient.ClassifyReloginResponse("status=0"));
        Assert.AreEqual(AhkReloginResponse.Expired, AhkPortalClient.ClassifyReloginResponse("8"));
        Assert.AreEqual(AhkReloginResponse.Expired, AhkPortalClient.ClassifyReloginResponse("status=8"));
        Assert.AreEqual(AhkReloginResponse.Expired,
            AhkPortalClient.ClassifyReloginResponse("<html><form action='/Home/_Login'>status=0</form></html>"));
        Assert.AreEqual(AhkReloginResponse.Unknown, AhkPortalClient.ClassifyReloginResponse("\"\""));
        Assert.AreEqual(AhkReloginResponse.Unknown, AhkPortalClient.ClassifyReloginResponse("temporarily unavailable"));
        Assert.AreEqual(AhkReloginResponse.Unknown,
            AhkPortalClient.ClassifyReloginResponse("upstream error at 2026-08-21T10:08:00Z"));
    }

    [TestMethod]
    public void LoginBackoff_GrowsExponentiallyAndCaps()
    {
        Assert.AreEqual(TimeSpan.FromMinutes(1), AhkSessionRetryPolicy.LoginBackoff(1, 60, 900));
        Assert.AreEqual(TimeSpan.FromMinutes(2), AhkSessionRetryPolicy.LoginBackoff(2, 60, 900));
        Assert.AreEqual(TimeSpan.FromMinutes(15), AhkSessionRetryPolicy.LoginBackoff(10, 60, 900));
    }

    [TestMethod]
    public void FailedLogin_NeverRetriesInsideTheBrokerBlockingInterval()
    {
        var attempt = new DateTime(2026, 8, 21, 10, 0, 0, DateTimeKind.Utc);
        var failure = attempt.AddSeconds(20);

        Assert.AreEqual(
            attempt.AddMinutes(10),
            AhkSessionRetryPolicy.NextLoginAttemptUtc(failure, attempt, 1, 60, 900, 600));
        Assert.AreEqual(
            failure.AddMinutes(15),
            AhkSessionRetryPolicy.NextLoginAttemptUtc(failure, attempt, 10, 60, 900, 600));
    }

    [TestMethod]
    public void KeepAliveBackoff_NeverRetriesFasterThanTheNormalKeepalive()
    {
        Assert.AreEqual(TimeSpan.FromMinutes(1), AhkSessionRetryPolicy.KeepAliveBackoff(1, 60, 900));
        Assert.AreEqual(TimeSpan.FromMinutes(4), AhkSessionRetryPolicy.KeepAliveBackoff(3, 60, 900));
        Assert.AreEqual(TimeSpan.FromMinutes(15), AhkSessionRetryPolicy.KeepAliveBackoff(20, 60, 900));
    }
}
