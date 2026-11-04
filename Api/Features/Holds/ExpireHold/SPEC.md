# CAP-HOLD-006 — Expire hold

- Capability: CAP-HOLD-006
- Canonical spec: `capabilities/holds/features/hold-expiry.md` in `spec-RouteGo.SeatInventory.API` (version 0.1.0); ADR 0006
- Area: Holds
- Slice: `Features/Holds/ExpireHold/`

## Contract

`kind: worker` — no HTTP operation. Trigger `queue: hold-expiry` (`HoldExpiryQueueUrl`), read
**in-process** by `ExpireHoldService` (a `BackgroundService`, 20-second long polling, up to 10
timers per receive). One timer is `ExpireHoldHandler.HandleAsync`.

- Message: `HoldExpiryTimerMessage` — the body CreateHold sends (`tripId`, `bookingId`,
  `holdExpiresAt`, `seatNos`, `correlationId`), delivered at the hold expiry.
- A timer is deleted from the queue only after it is handled; a failure leaves it for the next delivery.
- Emits: `HOLD_EXPIRY_FAILED` — one Error log line (`tripId`, `bookingId`, `holdExpiresAt`) when the
  5th delivery fails; the queue's redrive policy (`maxReceiveCount` 5) then parks it on its dead-letter queue.

## Acceptance criteria

Each criterion maps to tests in `ExpireHold.Tests.cs`.

- [x] AC-1: An unpaid hold frees itself at its expiry — `HandleAsync_UnpaidHoldAtItsExpiry_FreesItsSeatsTogetherWithReasonHoldExpired`
  (asserts the seat change record the seat-change stream turns into *SeatReleased*, reason `HoldExpired`)
- [x] AC-2: A paid hold is left alone — `HandleAsync_HoldAlreadyPaid_LeavesTheSeatsBooked`
- [x] AC-3: A hold already released is left alone — `HandleAsync_HoldReleasedAndSeatHeldByAnotherBooking_LeavesTheOtherHoldAlone`
- [x] AC-4: A stale timer never frees a newer hold of the same booking — `HandleAsync_StaleTimerOfAHoldMadeAgain_LeavesTheNewerHoldAlone`
- [x] AC-5: The timer survives a restart (it waits on the queue) — `ExpireDueHoldsAsync_TimerWaitingOnTheQueue_FreesTheSeatsAndRemovesTheTimer`
- [x] AC-6: Expiry on a cancelled trip blocks the seats — `HandleAsync_CancelledTrip_BlocksTheSeatsInsteadOfFreeingThem`
- [x] AC-7: The timer fires twice — `HandleAsync_TimerDeliveredTwice_SecondChangesNothing`
- [x] AC-8: Repeated failure leaves the timer queued and the seats Held — `ExpireDueHoldsAsync_SeatStoreUnavailable_LeavesTheTimerOnTheQueue`
  (the alert on the last delivery and the parking are the redrive policy's; not tested here)

## Invariants touched

- Only the timer ends a Held hold on its own; no time-to-live (ADR 0006).
- Repeat safety: per seat `status = Held AND bookingId = <booking> AND holdExpiresAt = <the timer's>`,
  in one `TransactWriteItems`. A lost race re-reads and decides again (at most 3 attempts).
- A cancelled trip's seats never return to sale (ADR 0007): freed seats go to Blocked
  (`blockReason = TripCancelled`); a release to Available carries a `ConditionCheck` that the trip is still open.

## Seat change record written (read by the seat-change stream, CAP-SEAT-008)

On every seat freed: `status = Available` (or `Blocked` + `blockReason = TripCancelled`),
`lastChangeReason = HoldExpired`, `lastChangeRequest` (one new UUID for all seats of the timer),
`lastChangeCorrelationId` (the hold request's, from the timer), `lastChangedAt`; removes `bookingId`,
`holdExpiresAt`, `lastChangedBy`, `lastChangeNote`, `lastChangeMovedFrom`. The freed booking is in
the change's old image.

## Notes

- `MaxDeliveries` (5) in `ExpireHoldService` must match the queue's `maxReceiveCount`.
- Every running copy of the API reads the queue; SQS hands each timer to one copy at a time.
- `check_structure.py` reports SL3 (no controller) for this slice: the API slice checker has no worker kind. Identity's `PublishIdentityEvents` worker slice gets the same finding.
