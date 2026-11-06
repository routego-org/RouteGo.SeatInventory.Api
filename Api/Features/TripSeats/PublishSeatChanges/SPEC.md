# CAP-SEAT-008 — Publish seat changes

- Capability: CAP-SEAT-008
- Canonical spec: `capabilities/trip-seats/features/seat-event-publishing.md` in `spec-RouteGo.SeatInventory.API` (version 0.1.1); events: `architecture/seat-events.md` (0.1.0); ADR 0005
- Area: TripSeats
- Slice: `Features/TripSeats/PublishSeatChanges/`

## Contract

`kind: worker` — no HTTP operation. Trigger `stream: seat-changes`: the seat store table's DynamoDB
Stream (`NEW_AND_OLD_IMAGES`), read **in-process** by `PublishSeatChangesService` (a
`BackgroundService`, every 5 seconds) — the hosting style of Identity's `PublishIdentityEvents`, with
ADR 0005's stream as the source. One run is `PublishSeatChangesHandler.HandleAsync`.

- Reads: every shard of the table's latest stream, from the saved position (a parent shard is
  finished before its children); trip items for the operator of a block/unblock.
- Delivers to: `InventoryEventsTopicArn` (SeatHeld, SeatBooked, SeatReleased, SeatBlocked,
  SeatUnblocked, SeatChanged) or `AuditEventsTopicArn` (AuditRecorded — RouteGo's shared audit
  topic), message attribute `eventType`, RouteGo envelope (`eventId`, `eventType`, `version` 1,
  `occurredAt` = the change's `lastChangedAt`, `correlationId`, `producer` `seat-inventory-service`, `data`).
- Event identity: the seat event's `eventId` is the change's `lastChangeRequest`; the audit record's
  is derived from it (SHA-256 of request + `AuditRecorded`), so a repeat keeps both identities.
- Emits: `SEAT_EVENT_DELAYED` — one Error log line per change held back more than 5 minutes
  (`oldestChangeAt`, `lagSeconds`), and per change that can never become an event (parked).

## Acceptance criteria

Each criterion maps to tests in `PublishSeatChanges.Tests.cs`.

- [x] AC-1: One event per request, covering all its seats — `HandleAsync_TwoSeatsHeldInOneRequest_PublishesOneSeatHeldNamingBoth`
- [x] AC-2: Each kind of change gives its event — `HandleAsync_HoldConfirmed_PublishesSeatBooked`,
  `HandleAsync_HoldExpiredOnCancelledTrip_PublishesSeatReleasedToBlockedNamingTheFreedBooking`,
  `HandleAsync_OperatorBlocksSeats_PublishesSeatBlockedAndAnAuditRecord`,
  `HandleAsync_SeatsChanged_PublishesSeatChangedWithEveryMove`
- [x] AC-3: Changes that announce nothing — `HandleAsync_SeatsCreatedFareChangedOrCancellationBlocked_PublishesNothing`
- [x] AC-4: Staff actions give an audit record — `HandleAsync_OperatorBlocksSeats_PublishesSeatBlockedAndAnAuditRecord`,
  `HandleAsync_AdminCancelsHold_PublishesSeatReleasedAndAnAuditRecordWithTheNote`
- [x] AC-5: No change without its event, even across a crash — the position is saved only after
  delivery: `HandleAsync_MessagingDownThenBack_KeepsChangesThenDeliversThemAll`,
  `HandleAsync_DeliveryNotConfirmed_RepublishesWithTheSameEventId`
- [x] AC-6: A redelivered event keeps its event ID — `HandleAsync_DeliveryNotConfirmed_RepublishesWithTheSameEventId`
- [x] AC-7: Delivery resumes after an outage — `HandleAsync_MessagingDownThenBack_KeepsChangesThenDeliversThemAll`
- [x] AC-8: Alert when more than 5 minutes behind, once — `HandleAsync_ChangeWaitingOverFiveMinutes_RaisesTheDelayAlertOnce`
- One publisher at a time — `HandleAsync_AnotherCopyHoldsTheLease_SkipsAndPublishesNothing`
- Unreadable change parked, later changes still published — `HandleAsync_UnreadableChange_ReportsItAndStillPublishesLaterChanges`

## Invariants touched

- One seat's events in order: a shard stops at the first event it cannot deliver; changes after it
  wait for the next run. Order across shards (seats of different trips) is not kept, as the spec allows.
- One publisher at a time across every running copy of the API (LMI runs 3 or more): a lease item
  (`pk = PUBLISHER#SeatChanges`, `sk = LEASE`, 30 s, renewed each run) taken with a conditional write.
- Progress per shard (`sk = SHARD#<shardId>`: `sequenceNumber`, `isFinished`, `savedAt`,
  `alertedRequest`) lives in the seat store too. Those writes are on the stream themselves, so
  progress made only past the publisher's own items is saved at most once a minute.
- Announces nothing: seat creation (INSERT), seat removal, reasons `TripCancelled` and `FareChanged`.
  An unknown reason, a request that is not an identifier, or a missing field is parked with
  `SEAT_EVENT_DELAYED`, never guessed at.

## Deployment

The table's stream must be on with `NEW_AND_OLD_IMAGES` (ADR 0005: "a deployment step, recorded in
the codebase"); without it every run fails naming the command. Locally `scripts/local-setup.sh` turns it on.

## Notes

- `occurredAt` is the stored `lastChangedAt` text (ISO-8601 UTC); `holdExpiresAt` is passed through as stored.
- The stream keeps records 24 hours; an outage longer than that loses the oldest changes.
- `check_structure.py` reports SL3 (no controller) for this slice: the API slice checker has no worker kind. Identity's `PublishIdentityEvents` worker slice gets the same finding.

## Shared with CAP-SEAT-009

- The event wire shapes, `SeatEventSender` and envelope helpers live in `Common/Messaging/SeatEventMessages.cs`, because
  `RepublishSeats` sends the same events. `SeatHeld`, `SeatBooked` and `SeatBlocked` carry an optional `republished`
  field that this publisher never sets (`HandleAsync_RealSeatChange_PublishesEventWithoutTheRepublishedField`).
