# CAP-HOLD-008 — Release cancelled booking

- Capability: CAP-HOLD-008
- Canonical spec: `capabilities/holds/features/booking-seat-release.md` in `spec-RouteGo.SeatInventory.API` (version 0.2.0); ADR 0007
- Area: Holds
- Slice: `Features/Holds/ReleaseCancelledBooking/`

## Contract

`kind: worker` — no HTTP operation. Trigger `topic: booking-cancelled`: the queue `BookingEventsQueueUrl`
(`inventory-booking-q`), shared with CAP-HOLD-007 and read by `Infrastructure/Messaging/BookingEventsService`, which
hands BookingCancelled to `ReleaseCancelledBookingHandler.HandleAsync` through `BookingEventDispatcher`.

- Data (`BookingCancelledMessage`): `bookingId`, `tripId`. Seat labels, refund amount and reason are not read.
  **Booking has no specs or code yet**: the shape is from the system event catalog and must be checked once Booking exists.
- Applied → message deleted. No `bookingId` or `tripId` → `BOOKING_EVENT_NOT_APPLIED` (reason `invalidEventData`),
  moved to `BookingEventsDeadLetterQueueUrl` at once. A failure → message left; on the 5th delivery reason
  `repeatedFailure`, then the redrive policy parks it.

## Acceptance criteria

Each criterion maps to tests in `ReleaseCancelledBooking.Tests.cs`; the queue paths are in
`Infrastructure/Messaging/BookingEventsService.Tests.cs`.

- [x] AC-4: A cancelled booking's seats are freed — `HandleAsync_CancelledBookingOwnsSeats_FreesThemTogetherWithReasonBookingCancelled`,
  `HandleAsync_CancelledBookingStillHoldsASeat_FreesItAsWell`
- [x] AC-5: The same cancellation twice — `HandleAsync_DeliveredAgainAfterTheSeatWasHeldByAnotherBooking_LeavesTheNewHoldAlone`
- [x] AC-6: On a cancelled trip the seats are blocked — `HandleAsync_CancelledTrip_BlocksTheSeatsInsteadOfFreeingThem`
- [x] AC-7: Only the booking's own seats are freed — `HandleAsync_EventListsAnotherBookingsSeat_FreesOnlyTheBookingsOwnSeat`
- [x] AC-8: Unusable data and repeated failure — `HandleAsync_IdentifierMissing_RejectedAsInvalidEventData`; the queue
  paths in `BookingEventsServiceTests`

## Invariants touched

- The only way a Booked seat is freed. The seats freed are the ones recorded for the booking on the trip.
- Repeat safety: per seat `status = <Held or Booked, as read> AND bookingId = <booking>`, in one `TransactWriteItems`.
  A lost race re-reads and decides again (at most 3 attempts).
- A cancelled trip's seats never return to sale (ADR 0007): freed seats go to Blocked (`blockReason = TripCancelled`);
  a release to Available carries a `ConditionCheck` that the trip is still open.

## Seat change record written (read by the seat-change stream, CAP-SEAT-008)

On every seat freed: `status = Available` (or `Blocked` + `blockReason = TripCancelled`),
`lastChangeReason = BookingCancelled`, `lastChangeRequest` (one new UUID per event), `lastChangeCorrelationId`,
`lastChangedAt`; removes `bookingId`, `holdExpiresAt`, `lastChangedBy`, `lastChangeNote`, `lastChangeMovedFrom`.

## Notes

- `check_structure.py` reports SL3 (no controller) for this slice: the API slice checker has no worker kind.
