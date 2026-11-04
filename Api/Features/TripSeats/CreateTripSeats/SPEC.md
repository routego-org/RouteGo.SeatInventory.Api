# CAP-SEAT-005 — Create trip seats

- Capability: CAP-SEAT-005
- Canonical spec: `capabilities/trip-seats/features/trip-seat-creation.md` in `spec-RouteGo.SeatInventory.API` (version 0.1.0); `architecture/consumed-events.md`
- Area: TripSeats
- Slice: `Features/TripSeats/CreateTripSeats/`

## Contract

`kind: worker` — no HTTP operation. Trigger `topic: trip-scheduled`: the queue `TripScheduledQueueUrl`
(`inventory-trip-scheduled-q`), subscribed to SNS `routego-fleet-events` with raw delivery and filter
`eventType = TripScheduled`. Read **in-process** by `CreateTripSeatsService` (an `EventQueueService`:
20-second long polling, up to 10 events per receive). One event is `CreateTripSeatsHandler.HandleAsync`.

- Envelope: the RouteGo envelope (`eventId`, `eventType`, `version`, `occurredAt`, `correlationId`, `producer`, `data`).
- Data (`TripScheduledMessage`), Fleet's spelling, checked against `RouteGo.Fleet.Api/Domain/Trips/TripScheduledEvent.cs`:
  `tripId`, `operatorId` (an identifier), `departureUtc`, `seats[]` of `seatNo`, `deck` (`Lower`/`Upper`), `row`, `col`,
  `class`, `fare` (whole INR). Fleet's other fields are not read.
- Applied → message deleted. Unusable data → `FLEET_EVENT_NOT_APPLIED` (reason `invalidEventData`) and the message is
  moved to `TripScheduledDeadLetterQueueUrl` at once. A failure → message left; on the 5th delivery
  `FLEET_EVENT_NOT_APPLIED` (reason `repeatedFailure`), then the redrive policy parks it on the same dead-letter queue.

## Acceptance criteria

Each criterion maps to tests in `CreateTripSeats.Tests.cs`.

- [x] AC-1: A new trip gets its seats — `HandleAsync_NewTrip_CreatesTheTripAndEverySeatAvailable`,
  `HandleAsync_NewTrip_SeatHasItsPositionClassFareAndChangeRecord`, `HandleAsync_NewTrip_SeatMapCanBeRead` (through `GET /trips/{tripId}/seats`)
- [x] AC-2: The same event twice — `HandleAsync_SameEventAgainAfterASeatWasHeld_KeepsTheHoldAndTheSeatCount`
- [x] AC-3: A different event for a known trip — `HandleAsync_DifferentEventForAKnownTrip_ChangesNothing`
- [x] AC-4: A cancellation that arrived first — `HandleAsync_CancellationArrivedFirst_CreatesEverySeatBlockedForTheCancellation`
- [x] AC-5: Unusable event data — `HandleAsync_TripFieldMissing_RejectedAndNothingCreated`, `HandleAsync_SeatFieldMissing_RejectedAndNothingCreated`,
  `HandleAsync_TwoSeatsWithTheSameLabel_RejectedAndNothingCreated`, `HandleWaitingEventsAsync_UnusableData_SetsTheEventAsideOnTheDeadLetterQueue`
- [x] AC-6: Repeated temporary failure — `HandleWaitingEventsAsync_SeatStoreUnavailable_LeavesTheEventOnTheQueue`
  (the alert on the 5th delivery and the parking are the redrive policy's; not tested here)
- [x] AC-7: An announcement waiting while stopped — `HandleWaitingEventsAsync_EventWaitingOnTheQueue_CreatesTheSeatsAndRemovesTheEvent`
- Wrong subscription filter — `HandleWaitingEventsAsync_EventOfAnotherType_SetsItAsideOnTheDeadLetterQueue`

## Invariants touched

- All seats or none: the trip item and every seat are one `TransactWriteItems` (Fleet's buses have at most 60 seats;
  the transaction limit is 100 items).
- Repeat safety: the trip item's `seatsCreatedAt` — the write requires `attribute_not_exists(seatsCreatedAt)`, and each
  seat `attribute_not_exists(pk)`. A trip already holding `seatsCreatedAt` is left alone, whichever event names it.
- A cancelled trip's seats never go on sale (ADR 0007): seats of a trip already marked `isCancelled` are created
  `Blocked` + `blockReason = TripCancelled`; seats created `Available` need the trip still open, checked in the same
  transaction. A lost race re-reads and decides again (at most 3 attempts).

## Seat store items written

- Trip (`pk = TRIP#<tripId>`, `sk = TRIP`): `operatorId`, `departureAt` (ISO-8601 UTC), `seatsCreatedAt`,
  `isCancelled` (kept if an early TripCancelled set it, else `false`).
- Seat (`sk = SEAT#<seatNo>`): `seatNo`, `deck`, `row`, `column`, `seatClass`, `fare`, `status`, (`blockReason`),
  `lastChangeReason = Created`, `lastChangeRequest` (one new UUID for all seats), `lastChangeCorrelationId` (the
  event's), `lastChangedAt`. A new item is an INSERT, which the seat-change stream (CAP-SEAT-008) never announces.

## Notes

- `check_structure.py` reports SL3 (no controller) for this slice: the API slice checker has no worker kind.
- The trip records may be removed 7 days after departure (business rule 5); no time-to-live is set here — cleanup only.
