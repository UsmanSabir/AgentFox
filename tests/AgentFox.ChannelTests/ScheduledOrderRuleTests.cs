using TradingAgent.Market;
using TradingAgent.Watchlist;

namespace AgentFox.ChannelTests;

/// <summary>
/// The two time bounds of an armed order: when it starts looking, and when it gives up.
///
/// <para>
/// These rules decide which DAY an order reaches the market on, and every way of getting that wrong is
/// silent. A timezone slip moves it to the wrong session; an expiry anchored on the wrong instant makes
/// a whole class of scheduled orders expire before they ever become active, refused by a rule the
/// operator never set. Neither produces an error at the time — the order simply sits there and then
/// does the wrong thing, or nothing, weeks later.
/// </para>
/// </summary>
[TestClass]
public sealed class ScheduledOrderRuleTests
{
    // 2026-09-17 in Pakistan; the UTC instant is the evening before, which is itself one of the cases
    // worth pinning — see TheDateIsComparedInPakistan_NotInUtc.
    private static readonly DateOnly TodayPkt = new(2026, 9, 17);
    private static readonly DateTime NowUtc = new(2026, 9, 17, 5, 0, 0, DateTimeKind.Utc);

    private static readonly DateOnly TheTwentySecond = new(2026, 9, 22);

    /// <summary>PKT is UTC+5 with no DST, so midnight on the 22nd is 19:00Z on the 21st.</summary>
    private static readonly DateTime TwentySecondMidnightPkt =
        new(2026, 9, 21, 19, 0, 0, DateTimeKind.Utc);

    // ── The activation instant ───────────────────────────────────────────────

    [TestMethod]
    public void ADate_ResolvesToThatDatesMidnightInPakistan()
    {
        var window = Resolve(activeFrom: TheTwentySecond);

        Assert.IsTrue(window.Ok, window.Message);
        Assert.AreEqual(TwentySecondMidnightPkt, window.ActiveFromUtc,
            "The exchange trades on PKT. Resolving the date in UTC would arm the order five hours "
            + "into the wrong day.");
    }

    [TestMethod]
    public void PktHasNoDaylightSaving_SoTheOffsetIsTheSameInJanuaryAndJuly()
    {
        // Worth pinning rather than assuming: the whole conversion rests on it, and a host falling
        // back to a made-up zone is the way it would quietly stop being true.
        var january = Resolve(activeFrom: new DateOnly(2027, 1, 15));
        var july    = Resolve(activeFrom: new DateOnly(2027, 7, 15));

        Assert.AreEqual(new DateTime(2027, 1, 14, 19, 0, 0, DateTimeKind.Utc), january.ActiveFromUtc);
        Assert.AreEqual(new DateTime(2027, 7, 14, 19, 0, 0, DateTimeKind.Utc), july.ActiveFromUtc);
    }

    [TestMethod]
    public void NoDate_MeansActiveNow_AndIsNotAnError()
    {
        var window = Resolve(activeFrom: null);

        Assert.IsTrue(window.Ok);
        Assert.IsNull(window.ActiveFromUtc,
            "Null is how every order behaved before this existed, and the evaluator reads it as "
            + "\"active now\". It must not be materialised into an instant.");
    }

    [TestMethod]
    public void Today_IsAccepted()
    {
        // "Schedule it for today" is a legitimate thing to ask for mid-morning, and it is also what
        // an off-by-one in the comparison would break first.
        var window = Resolve(activeFrom: TodayPkt);

        Assert.IsTrue(window.Ok, window.Message);
    }

    [TestMethod]
    public void APastDate_IsRefused()
    {
        var window = Resolve(activeFrom: TodayPkt.AddDays(-1));

        Assert.IsFalse(window.Ok);
        Assert.AreEqual("active_from_in_past", window.ErrorCode);
    }

    [TestMethod]
    public void TheDateIsComparedInPakistan_NotInUtc()
    {
        // 22:00 PKT on the 17th is still the 17th at the exchange, but 17:00Z — and a rule comparing
        // the operator's date against DateTime.UtcNow.Date would agree here and disagree after
        // midnight PKT, refusing "tomorrow" for the five hours when it is most likely to be typed.
        var lateEvening = new DateTime(2026, 9, 17, 17, 0, 0, DateTimeKind.Utc);

        var window = ScheduledOrderRule.Resolve(
            activeFromDate: TodayPkt.AddDays(1),
            expiresUtc: null,
            expiresInDays: null,
            todayPkt: TodayPkt,
            nowUtc: lateEvening);

        Assert.IsTrue(window.Ok, window.Message);
    }

    // ── The expiry, and its anchor ───────────────────────────────────────────

    [TestMethod]
    public void WithNoDate_TheDefaultExpiryIsMeasuredFromNow()
    {
        // The pre-existing behaviour, pinned so the new anchor cannot change it by accident.
        var window = Resolve(activeFrom: null);

        Assert.AreEqual(NowUtc.AddDays(30), window.ExpiresUtc);
    }

    [TestMethod]
    public void WithADate_TheDefaultExpiryIsMeasuredFromACTIVATION()
    {
        var window = Resolve(activeFrom: TheTwentySecond);

        Assert.AreEqual(TwentySecondMidnightPkt.AddDays(30), window.ExpiresUtc,
            "\"Expires in 30 days\" is how long to keep TRYING. Measured from today, the waiting "
            + "would eat the allowance.");
    }

    [TestMethod]
    public void ADistantDate_WouldExpireBeforeItActivated_IfTheAnchorWereNow()
    {
        // The case the anchor exists for. 90 days out with the default 30-day expiry: anchored on
        // now this could never fire, and the operator set neither number.
        var window = Resolve(activeFrom: TodayPkt.AddDays(90));

        Assert.IsTrue(window.Ok, window.Message);
        Assert.IsTrue(window.ExpiresUtc > window.ActiveFromUtc);
    }

    [TestMethod]
    public void AnExplicitExpiry_IsTakenExactlyAsSent()
    {
        var explicitExpiry = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc);

        var window = Resolve(activeFrom: TheTwentySecond, expiresUtc: explicitExpiry);

        Assert.AreEqual(explicitExpiry, window.ExpiresUtc,
            "A caller that named an instant gets that instant. Only the DEFAULT moved its anchor.");
    }

    [TestMethod]
    public void AnExplicitExpiryBeforeActivation_IsRefused()
    {
        var window = Resolve(
            activeFrom: TheTwentySecond,
            expiresUtc: new DateTime(2026, 9, 19, 0, 0, 0, DateTimeKind.Utc));

        Assert.IsFalse(window.Ok);
        Assert.AreEqual("expires_before_active", window.ErrorCode);
        StringAssert.Contains(window.Message!, "could never fire");
    }

    [TestMethod]
    public void AnExpiryExactlyAtActivation_IsRefused()
    {
        // Not a rounding quibble: ArmedOrderEvaluator ages an order out on `now >= expiry` before it
        // checks activation, so an order expiring at the instant it activates is terminal on arrival.
        var window = Resolve(activeFrom: TheTwentySecond, expiresUtc: TwentySecondMidnightPkt);

        Assert.IsFalse(window.Ok);
        Assert.AreEqual("expires_before_active", window.ErrorCode);
    }

    [TestMethod]
    public void TheExpiryDayCount_IsClampedToAYear()
    {
        var window = Resolve(activeFrom: null, expiresInDays: 10_000);

        Assert.AreEqual(NowUtc.AddDays(ScheduledOrderRule.MaxExpiryDays), window.ExpiresUtc);
    }

    // ── What a Scheduled order may carry ─────────────────────────────────────

    [TestMethod]
    public void AScheduledRequest_WithOnlyADate_IsAccepted()
    {
        Assert.IsNull(ScheduledOrderRule.ValidateScheduledRequest(
            TheTwentySecond, triggerPrice: null, triggerPercent: null, triggerAlertKind: null));
    }

    [TestMethod]
    public void AScheduledRequest_WithoutADate_IsRefused()
    {
        var problem = ScheduledOrderRule.ValidateScheduledRequest(
            null, triggerPrice: null, triggerPercent: null, triggerAlertKind: null);

        Assert.AreEqual("scheduled_needs_a_date", problem?.Code);
    }

    [TestMethod]
    [DataRow(45.0, null, null, DisplayName = "a price level")]
    [DataRow(null, 3.0, null, DisplayName = "a percentage")]
    [DataRow(null, null, "BounceOffSupport", DisplayName = "an alert kind")]
    public void AScheduledRequest_CarryingACondition_IsRefusedRatherThanStripped(
        double? price, double? percent, string? alertKind)
    {
        // Stripping would store an order the operator believes is conditional and the evaluator treats
        // as unconditional — the one disagreement here that ends in shares nobody asked for.
        var problem = ScheduledOrderRule.ValidateScheduledRequest(
            TheTwentySecond,
            price == null ? null : (decimal)price,
            percent == null ? null : (decimal)percent,
            alertKind);

        Assert.AreEqual("scheduled_trigger_takes_no_condition", problem?.Code);
    }

    [TestMethod]
    public void PktResolution_MatchesPsxTimesOwnZone()
    {
        // Ties this rule to the one clock the calendar, the candle loader and the scanner all use, so
        // a host without a tz database cannot leave this converting against a different offset.
        Assert.AreEqual(
            TimeSpan.FromHours(5),
            PsxTime.Zone.GetUtcOffset(TheTwentySecond.ToDateTime(TimeOnly.MinValue)));
    }

    private static ScheduledOrderWindow Resolve(
        DateOnly? activeFrom, DateTime? expiresUtc = null, int? expiresInDays = null) =>
        ScheduledOrderRule.Resolve(activeFrom, expiresUtc, expiresInDays, TodayPkt, NowUtc);
}
