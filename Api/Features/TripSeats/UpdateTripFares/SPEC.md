# CAP-SEAT-006 — Update trip fares

- Capability: CAP-SEAT-006
- Canonical spec: `capabilities/trip-seats/features/trip-fare-update.md` in `spec-RouteGo.SeatInventory.API` (version 0.1.0); ADR 0008
- Area: TripSeats
- Slice: `Features/TripSeats/UpdateTripFares/`

## Contract

`kind: worker` — no HTTP operation. Trigger `topic: fare-changed`: the queue `FareChangedQueueUrl`
(`inventory-fare-changed-q`), subscribed to SNS `routego-fleet-events` with raw delivery and filter
`eventType = FareChanged`. Read **in-process** by `UpdateTripFaresService` (an `EventQueueService`). One event is
`UpdateTripFaresHandler.HandleAsync`.

- Data (`FareChangedMessage`), checked against `RouteGo.Fleet.Api/Domain/Trips/FareChangedEvent.cs`: `tripId`,
  `seats[]` of `seatNo`, `fare` (whole INR). Fleet's `classFares` and each seat's `class` are not read.
- The envelope's `occurredAt` orders fare changes.
- Applied (or stale) → message deleted. Unusable data → `FLEET_EVENT_NOT_APPLIED` (reason `invalidEventData`), moved to
  `FareChangedDeadLetterQueueUrl` at once. Trip not known yet → message left; on the 5th delivery
  `FLEET_EVENT_NOT_APPLIED` (reason `tripUnknown`). A failure → message left; on the 5th delivery reason
  `repeatedFailure`. Either way the redrive policy then parks it.

## Acceptance criteria

Each criterion maps to tests in `UpdateTripFares.Tests.cs`.

- [x] AC-1: Fares are replaced — `HandleAsync_FaresChanged_ReplacesTheFareOfEverySeatWhateverItsStatus`,
  `HandleAsync_FaresChanged_RecordsTheFareChangeAsOneChange` (reason `FareChanged`, which publishes nothing)
- [x] AC-2: The same event twice — `HandleAsync_SameEventAgain_ChangesNothing`
- [x] AC-3: The latest change wins — `HandleAsync_OlderChangeArrivingLate_KeepsTheLatestFare`
- [x] AC-4: A change for a trip not known yet waits — `HandleAsync_TripNotKnownYet_PostponedAsTripUnknown`,
  `HandleAsync_RetriedAfterTheTripArrived_SetsTheNewFares`, `HandleWaitingEventsAsync_TripNotKnownYet_LeavesTheEventOnTheQueue`
- [x] AC-5: A trip that never arrives — the event stays on the queue (test above); the `tripUnknown` alert on the 5th
  delivery and the parking are the redrive policy's; not tested here
- [x] AC-6: Unusable event data — `HandleAsync_FieldMissing_RejectedAsInvalidEventData`, `HandleAsync_SeatWithoutFare_RejectedAndNoFareChanges`,
  `HandleAsync_SeatTheTripDoesNotHave_RejectedAndNoFareChanges`, `HandleWaitingEventsAsync_UnusableData_SetsTheEventAsideOnTheDeadLetterQueue`

## Invariants touched

- All fares or none: the trip's `faresChangedAt` and every named seat's fare are one `TransactWriteItems`.
- Latest wins / repeat safety: the trip keeps `faresChangedAt` = the `occurredAt` of the last applied change; the write
  requires `faresChangedAt < :occurred` (ISO-8601 UTC, seven fraction digits, so text order is time order). An event
  not later than it changes nothing. A lost race re-reads and decides again (at most 3 attempts).
- Seat status, booking and hold expiry are never touched — only `fare` and the change record.

## Seat change record written (read by the seat-change stream, CAP-SEAT-008)

On every re-priced seat: `fare`, `lastChangeReason = FareChanged` (silent — no seat event), `lastChangeRequest` (one
new UUID per event), `lastChangeCorrelationId` (the event's), `lastChangedAt`; removes `lastChangedBy`,
`lastChangeNote`, `lastChangeMovedFrom`.

## Notes

- With the default 30-second visibility timeout, a FareChanged for an unknown trip is tried 5 times over roughly
  2–3 minutes before it is parked.
- `check_structure.py` reports SL3 (no controller) for this slice: the API slice checker has no worker kind.
