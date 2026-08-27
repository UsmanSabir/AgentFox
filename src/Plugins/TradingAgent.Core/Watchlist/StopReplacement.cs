using TradingAgent.Broker;

namespace TradingAgent.Watchlist;

/// <summary>What opening a replacement window concluded.</summary>
public enum StopReplacementOutcome
{
    /// <summary>The old order is confirmed gone and the replacement should be placed now.</summary>
    PlaceReplacement,

    /// <summary>
    /// Nothing was cancelled. The predecessor's order is still resting, so the position is still
    /// protected at the old trigger and the raise is retried on a later pass.
    /// </summary>
    HoldOldOrderIntact
}

/// <param name="CoverArmed">
/// Whether local cover was confirmed armed before anything was cancelled. False alongside
/// <see cref="StopReplacementOutcome.HoldOldOrderIntact"/> is the "could not cover it, so did not touch
/// it" case, which reads differently in a log than a cancel that was attempted and failed.
/// </param>
public sealed record StopReplacementResult(
    StopReplacementOutcome Outcome,
    string Reason,
    bool CoverArmed = false,
    bool CancelAttempted = false);

/// <summary>
/// The side effects <see cref="StopReplacement"/> needs, as four narrow functions.
///
/// <para>
/// <b>Why delegates rather than the repository interface.</b> This sequencer is the one code path in
/// the protective-stop machinery that cancels a live broker order, so its failure ladder is the part
/// that most needs deterministic tests — and it was the part that had none, because reaching it meant
/// constructing a <c>TradingManager</c>, an <c>ApprovalGate</c>, a broker reader and a 70-member
/// repository. Four functions can be supplied as lambdas in a test and wired to the real worker in
/// production, which is the difference between a covered failure ladder and a hope.
/// </para>
/// </summary>
/// <param name="ArmCoverAsync">Arms the local backstop for the successor at the quantity given.</param>
/// <param name="ReloadStopAsync">
/// Re-reads a stop from durable storage. Used to CONFIRM cover was actually recorded rather than
/// trusting that the write happened — unconfirmed cover is treated as no cover.
/// </param>
/// <param name="CancelOrderAsync">
/// Cancels one broker order and proves the result against the outstanding book. Only
/// <see cref="BrokerCancellationResult.Gone"/> counts.
/// </param>
/// <param name="RetirePredecessorAsync">
/// Moves the predecessor to its terminal state now that its order is gone. Returns false when the
/// transition did not apply (someone else already moved it), which is not an error.
/// </param>
public sealed record StopReplacementPorts(
    Func<ProtectiveStop, int, CancellationToken, Task> ArmCoverAsync,
    Func<string, CancellationToken, Task<ProtectiveStop?>> ReloadStopAsync,
    Func<string, CancellationToken, Task<BrokerCancellationResult>> CancelOrderAsync,
    Func<ProtectiveStop, string, CancellationToken, Task<bool>> RetirePredecessorAsync);

/// <summary>
/// Break-before-make: cover the position locally, cancel the predecessor's native order, retire its
/// row — then, and only then, tell the caller to place the replacement.
///
/// <para>
/// <b>Why break-before-make at all.</b> This broker sizes every SELL against custody MINUS the
/// quantity already committed to resting SELLs (confirmed live 2026-08-27: "You cannot sell more than
/// 0 shares of SYS"). While the predecessor rests over the whole position there is nothing to place
/// the replacement against, so make-before-break cannot complete and no amount of retrying changes
/// that. Cancelling first is the only route — which means deliberately opening a moment with no native
/// stop at the broker, and that moment is what this class exists to make survivable rather than merely
/// short.
/// </para>
///
/// <para>
/// <b>Every failure leaves MORE protection, never less.</b> Cover cannot be armed or confirmed →
/// nothing is cancelled. The cancel throws → nothing is cancelled. The cancel is accepted but cannot be
/// VERIFIED gone → treated as still resting, nothing advances. Only a cancel proven gone advances the
/// state, and it advances it durably before any placement is attempted, so a crash in the gap resumes
/// correctly instead of losing track of which order exists.
/// </para>
///
/// <para>
/// <b>There is no rollback, deliberately.</b> Once the cancel is confirmed the shares are free, so a
/// placement failure afterwards is transient (a socket, a refused approval) rather than structural, and
/// the right response is the caller's next-pass retry — not re-placing the OLD, lower stop and having
/// to supersede it all over again. The honest cost: between the cancel and a successful placement the
/// position is covered only by the local backstop, which needs the host process running.
/// </para>
/// </summary>
public static class StopReplacement
{
    public static async Task<StopReplacementResult> OpenWindowAsync(
        ProtectiveStop successor,
        ProtectiveStop predecessor,
        decimal? heldQuantity,
        string why,
        StopReplacementPorts ports,
        CancellationToken ct)
    {
        if (predecessor.LastOrderNo is not { Length: > 0 } orderNo)
            // DecideSupersede would not route here, but if it ever does there is nothing at the broker
            // holding the shares — so there is nothing to cancel and nothing to wait for.
            return new(StopReplacementOutcome.PlaceReplacement,
                "The stop being replaced has no broker order, so nothing is holding the shares.");

        // ── Cover first ──────────────────────────────────────────────────────
        // A raised stop is written straight to "active" by whoever raised it, so unlike a stop that grew
        // out of a pending fill it has never been through the backstop-arming path and has no local
        // cover at all. Arming it here is what makes the coming gap survivable.
        var covered = successor.LocalBackstopArmedId is not null;
        if (!covered)
        {
            if (heldQuantity is not { } held)
                return new(StopReplacementOutcome.HoldOldOrderIntact,
                    "Holdings could not be read, so local cover for the changeover cannot be sized. "
                    + "The old order was left resting.");

            var quantity = Math.Min(successor.DesiredQuantity, (int)Math.Floor(held));
            if (quantity <= 0)
                return new(StopReplacementOutcome.HoldOldOrderIntact,
                    "There is nothing confirmed to cover, so the old order was left resting.");

            await ports.ArmCoverAsync(successor, quantity, ct);

            // Confirm from storage rather than assuming the write landed: unconfirmed cover is no cover,
            // and this is the last point at which doing nothing is still safe.
            var reloaded = await ports.ReloadStopAsync(successor.StopId, ct);
            if (reloaded?.LocalBackstopArmedId is null)
                return new(StopReplacementOutcome.HoldOldOrderIntact,
                    "Local cover for the changeover could not be confirmed armed, so nothing was "
                    + "cancelled and the position stays protected at the old trigger.");

            covered = true;
        }

        BrokerCancellationResult cancellation;
        try
        {
            cancellation = await ports.CancelOrderAsync(orderNo, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new(StopReplacementOutcome.HoldOldOrderIntact,
                $"The cancel of order {orderNo} failed outright ({ex.Message}), so it is assumed to be "
                + "still resting and the raise is retried later.", covered, CancelAttempted: true);
        }

        if (!cancellation.Gone)
            // Not PROVEN gone. The book is the only authority: an accepted request that cannot be
            // verified is exactly the case where assuming success would leave the position bare.
            return new(StopReplacementOutcome.HoldOldOrderIntact,
                $"Order {orderNo} is not confirmed cancelled ({cancellation.Message}), so it is treated "
                + "as still resting and still protecting the position.", covered, CancelAttempted: true);

        // Confirmed gone. Close the predecessor BEFORE the caller places anything, so durable state can
        // only ever say "the old order is gone" once it actually is.
        await ports.RetirePredecessorAsync(
            predecessor,
            $"Superseded by {successor.StopId}; its order {orderNo} was cancelled to free the shares "
            + $"for the replacement. {why}",
            ct);

        return new(StopReplacementOutcome.PlaceReplacement,
            $"Order {orderNo} is confirmed cancelled; the local backstop covers the position until the "
            + "replacement is resting at the broker.", covered, CancelAttempted: true);
    }
}
