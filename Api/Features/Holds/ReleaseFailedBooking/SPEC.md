# CAP-HOLD-007 — Release failed booking

- Capability: CAP-HOLD-007
- Canonical spec: `capabilities/holds/features/booking-seat-release.md` in `spec-RouteGo.SeatInventory.API` (version 0.2.0)
- Area: Holds
- Slice: `Features/Holds/ReleaseFailedBooking/`

## Contract

`kind: worker` — no HTTP operation. Trigger `topic: booking-failed`: the queue `BookingEventsQueueUrl`
(`inventory-booking-q`), subscribed to SNS `routego-booking-events` with raw delivery and filter
`eventType` in (`BookingFailed`, `BookingCancelled`) — the queue the system queue catalog names. That queue is
shared with CAP-HOLD-008, so it is read by `Infrastructure/Messaging/BookingEventsService` (an
`EventQueueService`), which hands BookingFailed to `ReleaseFailedBookingHandler.HandleAsync` through
`BookingEventDispatcher`.

- Data (`BookingFailedMessage`): `bookingId`, `tripId`. **Booking has no specs or code yet**: the shape is from the
  system event catalog (*00 – Overall architecture* §9) and must be checked once Booking exists (the spec's open question).
- Applied → message deleted. No `bookingId` or `tripId` → `BOOKING_EVENT_NOT_APPLIED` (reason `invalidEventData`),
  moved to `BookingEventsDeadLetterQueueUrl` at once. A failure → message left; on the 5th delivery
  `BOOKING_EVENT_NOT_APPLIED` (reason `repeatedFailure`), then the redrive policy parks it.

## Acceptance criteria

Each criterion maps to tests in `ReleaseFailedBooking.Tests.cs`; the queue paths are in
`Infrastructure/Messaging/BookingEventsService.Tests.cs`.

- [x] AC-1: A failed booking's held seats are freed — `HandleAsync_FailedBookingHoldsSeats_FreesThemTogetherWithReasonBookingFailed`
  (asserts the change record the seat-change stream turns into *SeatReleased*, reason `BookingFailed`)
- [x] AC-2: BookingFailed never frees a booked seat — `HandleAsync_BookingAlreadyBooked_LeavesTheSeatsBooked`
- [x] AC-3: Nothing left to free — `HandleAsync_DeliveredTwice_SecondChangesNothing`, `HandleAsync_SeatHeldByAnotherBooking_LeavesTheOtherHoldAlone`
- [x] AC-8: Unusable data and repeated failure — `HandleAsync_IdentifierMissing_RejectedAsInvalidEventData`;
  `BookingEventsServiceTests.HandleWaitingEventsAsync_NoBookingId_SetsTheEventAsideOnTheDeadLetterQueue`,
  `BookingEventsServiceTests.HandleWaitingEventsAsync_SeatStoreUnavailable_LeavesTheEventOnTheQueue`
- Business rule 5 (trip not known) — `HandleAsync_TripNotKnown_AppliedWithNothingToFree`
- Business rule 6 (cancelled trip) — `HandleAsync_CancelledTrip_BlocksTheSeatsInsteadOfFreeingThem`

## Invariants touched

- The seats freed are the ones recorded for the booking on the trip; seat labels in the event are not read.
- Repeat safety: per seat `status = Held AND bookingId = <booking>`, in one `TransactWriteItems`. A lost race re-reads
  and decides again (at most 3 attempts).
- A cancelled trip's seats never return to sale (ADR 0007): freed seats go to Blocked (`blockReason = TripCancelled`);
  a release to Available carries a `ConditionCheck` that the trip is still open.

## Seat change record written (read by the seat-change stream, CAP-SEAT-008)

On every seat freed: `status = Available` (or `Blocked` + `blockReason = TripCancelled`),
`lastChangeReason = BookingFailed`, `lastChangeRequest` (one new UUID per event), `lastChangeCorrelationId` (the
event's), `lastChangedAt`; removes `bookingId`, `holdExpiresAt`, `lastChangedBy`, `lastChangeNote`,
`lastChangeMovedFrom`. The freed booking is in the change's old image.

## Notes

- `check_structure.py` reports SL3 (no controller) for this slice: the API slice checker has no worker kind.
