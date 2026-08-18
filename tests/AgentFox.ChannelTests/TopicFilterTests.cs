using AgentFox.Plugins.Channels;

namespace AgentFox.ChannelTests;

/// <summary>
/// The wildcard semantics, pinned. These are the cases that decide whether a subscription written
/// months ago still means what its author thought — the whole point of choosing single-segment
/// <c>*</c> over "matches everything after" was that the answer stays the same as topics deepen.
/// </summary>
[TestClass]
public class TopicFilterTests
{
    [TestMethod]
    public void ExactTopic_MatchesItselfOnly()
    {
        Assert.IsTrue(TopicFilter.Matches("trading.order", "trading.order"));
        Assert.IsFalse(TopicFilter.Matches("trading.order", "trading.orders"));
        Assert.IsFalse(TopicFilter.Matches("trading.order", "trading"));
        Assert.IsFalse(TopicFilter.Matches("trading.order", "trading.order.accepted"));
    }

    [TestMethod]
    public void Star_MatchesExactlyOneSegment()
    {
        Assert.IsTrue(TopicFilter.Matches("trading.*", "trading.order"));
        Assert.IsTrue(TopicFilter.Matches("*.order", "trading.order"));
        Assert.IsTrue(TopicFilter.Matches("*.*", "trading.order"));

        // The distinction the whole scheme rests on: a single star never spans a deeper subject.
        Assert.IsFalse(TopicFilter.Matches("trading.*", "trading.order.accepted"));
        Assert.IsFalse(TopicFilter.Matches("*.order", "a.b.order"));
        Assert.IsFalse(TopicFilter.Matches("trading.*", "trading"));
    }

    [TestMethod]
    public void Gt_MatchesOneOrMoreTrailingSegments_ButNotTheRootAlone()
    {
        Assert.IsTrue(TopicFilter.Matches("trading.>", "trading.order"));
        Assert.IsTrue(TopicFilter.Matches("trading.>", "trading.order.accepted"));
        Assert.IsTrue(TopicFilter.Matches("trading.order.>", "trading.order.accepted"));

        // '>' stands for at least one segment: a channel watching the subtree did not ask for the
        // root event itself.
        Assert.IsFalse(TopicFilter.Matches("trading.>", "trading"));
        Assert.IsFalse(TopicFilter.Matches("trading.>", "hitl.approval"));
    }

    [TestMethod]
    public void BareGt_IsTheCatchAll()
    {
        Assert.IsTrue(TopicFilter.Matches(">", "trading"));
        Assert.IsTrue(TopicFilter.Matches(">", "trading.order"));
        Assert.IsTrue(TopicFilter.Matches(">", "a.b.c.d.e"));
    }

    [TestMethod]
    public void Matching_IsCaseInsensitive()
    {
        Assert.IsTrue(TopicFilter.Matches("Trading.Order", "trading.order"));
        Assert.IsTrue(TopicFilter.Matches("TRADING.>", "trading.order.accepted"));
    }

    [TestMethod]
    public void BlankSideNeverMatches()
    {
        Assert.IsFalse(TopicFilter.Matches(">", null));
        Assert.IsFalse(TopicFilter.Matches(">", "  "));
        Assert.IsFalse(TopicFilter.Matches(null, "trading.order"));
    }

    [TestMethod]
    public void Filter_RejectsGtBeforeTheEnd()
    {
        Assert.IsFalse(TopicFilter.IsValidFilter("trading.>.accepted", out var error));
        StringAssert.Contains(error!, ">");

        Assert.IsTrue(TopicFilter.IsValidFilter("trading.*.accepted", out _));
        Assert.IsTrue(TopicFilter.IsValidFilter("trading.>", out _));
    }

    [TestMethod]
    public void Topic_RejectsWildcards()
    {
        // Publishing on a wildcard would silently widen delivery rather than address a subject.
        Assert.IsFalse(TopicFilter.IsValidTopic("trading.*", out _));
        Assert.IsFalse(TopicFilter.IsValidTopic("trading.>", out _));
        Assert.IsFalse(TopicFilter.IsValidTopic("trading..order", out _));
        Assert.IsTrue(TopicFilter.IsValidTopic("trading.order-1_x", out _));
    }

    [TestMethod]
    public void Subscription_MatchesAnyOfItsFilters()
    {
        var subscription = TopicSubscription.Parse("trading.order.>, hitl.>");

        Assert.IsTrue(subscription.Matches("trading.order.accepted"));
        Assert.IsTrue(subscription.Matches("hitl.approval"));
        Assert.IsFalse(subscription.Matches("trading.stop.closed"));
        Assert.IsFalse(subscription.Matches("agent.notify"));
    }

    [TestMethod]
    public void Subscription_DefaultsToCatchAll_SoNothingSilentlyStopsBeingDelivered()
    {
        foreach (var spec in new string?[] { null, "", "   " })
        {
            var subscription = TopicSubscription.Parse(spec);
            Assert.IsTrue(subscription.IsCatchAll, $"spec '{spec}' should be the catch-all");
            Assert.IsTrue(subscription.Matches("anything.at.all"));
        }
    }

    [TestMethod]
    public void Subscription_WithOnlyInvalidFilters_FallsBackToCatchAll_NotToSilence()
    {
        var ok = TopicSubscription.TryParse("trading.>.oops", out var subscription, out var errors);

        Assert.IsFalse(ok);
        Assert.AreEqual(1, errors.Count);
        Assert.IsTrue(subscription.IsCatchAll, "a typo should make a channel noisier, never silent");
    }

    [TestMethod]
    public void Subscription_KeepsValidFiltersAndDropsBadOnes()
    {
        var ok = TopicSubscription.TryParse("trading.order.>, nope.>.nope", out var subscription, out var errors);

        Assert.IsFalse(ok);
        Assert.AreEqual(1, errors.Count);
        Assert.IsTrue(subscription.Matches("trading.order.accepted"));
        Assert.IsFalse(subscription.Matches("agent.notify"));
    }

    [TestMethod]
    public void Subscription_CollapsesToCatchAllWhenItContainsOne()
    {
        var subscription = TopicSubscription.Parse("trading.>, >");

        Assert.IsTrue(subscription.IsCatchAll);
        Assert.AreEqual(1, subscription.Filters.Count);
    }

    [TestMethod]
    public void Subscription_RoundTripsThroughItsStringForm()
    {
        var original = TopicSubscription.Parse("Trading.Order.>, HITL.>");
        var reparsed = TopicSubscription.Parse(original.ToString());

        CollectionAssert.AreEqual(original.Filters.ToArray(), reparsed.Filters.ToArray());
        Assert.AreEqual("trading.order.>, hitl.>", original.ToString());
    }

    [TestMethod]
    public void UnaddressedNotification_MatchesEverySubscription()
    {
        // Every caller that predates topics sends no topic at all; those must keep reaching
        // everyone rather than being filtered to nothing.
        var narrow = TopicSubscription.Parse("trading.order.>");

        Assert.IsTrue(narrow.Matches(null));
        Assert.IsTrue(narrow.Matches(""));
    }

    [TestMethod]
    public void HitlTopics_AreMandatory_AndOrdinaryOnesAreNot()
    {
        Assert.IsTrue(NotificationTopics.IsMandatory(NotificationTopics.HitlApproval));
        Assert.IsTrue(NotificationTopics.IsMandatory(NotificationTopics.HitlPlan));
        Assert.IsTrue(NotificationTopics.IsMandatory(NotificationTopics.HitlInput));
        Assert.IsTrue(NotificationTopics.IsMandatory("hitl.something.new"));

        Assert.IsFalse(NotificationTopics.IsMandatory(NotificationTopics.AgentNotify));
        Assert.IsFalse(NotificationTopics.IsMandatory("trading.order.accepted"));
        Assert.IsFalse(NotificationTopics.IsMandatory(null));
    }
}
