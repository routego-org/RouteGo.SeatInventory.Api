# CAP-SEAT-007 — Cancel trip

- Capability: CAP-SEAT-007
- Canonical spec: `capabilities/trip-seats/features/trip-cancellation-blocking.md` in `spec-RouteGo.SeatInventory.API` (version 0.1.0); ADR 0007
- Area: TripSeats
- Slice: `Features/TripSeats/CancelTrip/`

## Contract

`kind: worker` — no HTTP operation. Trigger `topic: trip-cancelled`: the queue `TripCancelledQueueUrl`
(`inventory-trip-cancelled-q`), subscribed to SNS `routego-fleet-events` with raw delivery and filter
`eventType = TripCancelled`. Read **in-process** by `CancelTripService` (an `EventQueueService`). One event is
`CancelTripHandler.HandleAsync`.

- Data (`TripCancelledMessage`), checked against Fleet's `CancelTrip`, `DeactivateSchedule` and `ReplaceSchedule`
  handlers: `tripId`, `reason` (kept on the log line only).
- Applied → message deleted. No `tripId` → `FLEET_EVENT_NOT_APPLIED` (reason `invalidEventData`), moved to
  `TripCancelledDeadLetterQueueUrl` at once. A failure → message left; on the 5th delivery `FLEET_EVENT_NOT_APPLIED`
  (reason `repeatedFailure`), then the redrive policy parks it.

## Acceptance criteria

Each criterion maps to tests in `CancelTrip.Tests.cs`.

- [x] AC-1: Free seats blocked, owned seats keep their booking — `HandleAsync_TripWithFreeHeldAndBookedSeats_BlocksOnlyTheFreeSeats`,
  `HandleAsync_FreeSeatsBlocked_RecordTheCancellationAsOneChange` (reason `TripCancelled`, which publishes nothing)
- [ ] AC-2: Owned seats blocked when released later — owned by the release slices (ExpireHold, ReleaseHold, CancelHold
  already release to Blocked on a cancelled trip; Booking's BookingCancelled is CAP-HOLD-008, not built yet)
- [ ] AC-3: Nothing on a cancelled trip can be sold — owned and tested by CreateHold / ConfirmHold (`TRIP_CANCELLED`)
- [x] AC-4: The same cancellation twice — `HandleAsync_CancellationDeliveredAgain_ChangesNothing`
- [x] AC-5: A cancellation before the trip — `HandleAsync_TripNotKnownYet_RemembersItAsCancelledWithoutSeats`
  (the seats created Blocked afterwards: CreateTripSeats' `HandleAsync_CancellationArrivedFirst_CreatesEverySeatBlockedForTheCancellation`)
- [x] AC-6: Unusable event data — `HandleAsync_NoTripId_RejectedAndNothingChanges`, `HandleWaitingEventsAsync_NoTripId_SetsTheEventAsideOnTheDeadLetterQueue`
- [x] AC-7: Repeated temporary failure — `HandleWaitingEventsAsync_SeatStoreUnavailable_LeavesTheEventOnTheQueue`,
  `HandleWaitingEventsAsync_EventWaitingOnTheQueue_CancelsTheTripAndRemovesTheEvent` (the alert and the parking are the redrive policy's)

## Invariants touched

- One `TransactWriteItems`: the trip's mark (`isCancelled = true`, `cancelledAt`) with condition "still open"; each
  Available or operator-blocked seat → `Blocked` + `blockReason = TripCancelled` with condition "status as read"; every
  other seat a `ConditionCheck` that its status is unchanged — so a seat released at the same moment can never stay
  Available on a cancelled trip. With no seats read, the mark also requires `attribute_not_exists(seatsCreatedAt)`.
  A lost race re-reads and decides again (at most 3 attempts).
- Repeat safety: a trip already `isCancelled = true` changes nothing.
- An unknown trip is remembered as `{ isCancelled = true, cancelledAt }` only — no `departureAt`, so its seat map stays
  `NOT_FOUND` until TripScheduled arrives, and CreateTripSeats then creates its seats Blocked.

## Seat change record written (read by the seat-change stream, CAP-SEAT-008)

On every blocked seat: `lastChangeReason = TripCancelled` (silent — no seat event), `lastChangeRequest` (one new UUID
per event), `lastChangeCorrelationId` (the event's), `lastChangedAt`; removes `lastChangedBy`, `lastChangeNote`,
`lastChangeMovedFrom`.

## Notes

- `check_structure.py` reports SL3 (no controller) for this slice: the API slice checker has no worker kind.
