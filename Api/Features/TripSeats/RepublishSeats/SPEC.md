# CAP-SEAT-009 — Republish seats

- Capability: CAP-SEAT-009
- Canonical spec: `capabilities/trip-seats/features/seat-republishing.md` in `spec-RouteGo.SeatInventory.API` (version 0.1.0); ADR 0010
- Area: TripSeats
- Slice: `Features/TripSeats/RepublishSeats/`

## Contract

`kind: worker` — no HTTP operation. Trigger `manual: seat-republish`: the platform engineer runs the API binary with
one of two options instead of serving, so the job is not reachable over the network. `Program.cs` parses the options
(`RepublishSeatsCommandLine`), runs `RepublishSeatsHandler.HandleAsync`, prints the report and returns before
`app.Run()`, so no background worker or web server starts.

- `Api --republish-trips <id,id,...>` — the named trips; or `Api --republish-all` — every known trip that has not
  departed and is not cancelled. Exactly one of the two. Optional `--correlation-id <text>`, generated when absent.
- Exit code: 0 when every chosen trip was re-published or skipped, 1 when any trip failed (re-run for the failed
  trips), 2 when the options are wrong.
- Report, one line per trip: `re-published with N events`, `skipped, departed|cancelled|not known`, or `FAILED, <why>`.

## Acceptance criteria

Each criterion maps to tests in `RepublishSeats.Tests.cs` (command line: `RepublishSeats.CommandLine.Tests.cs`).

- [x] AC-1: A named trip's taken seats are re-sent as their events — `HandleAsync_NamedTripWithTakenSeats_SendsEachTakenSeatAsItsEventMarkedRepublished`,
  `HandleAsync_SeatsChangedAtDifferentTimes_StampsEachEventWithItsLatestSeatChange`
- [x] AC-2: All upcoming trips — `HandleAsync_AllUpcoming_RepublishesUpcomingTripsAndSkipsDepartedAndCancelledOnes`
- [x] AC-3: A named trip that cannot be re-published is skipped and reported — `HandleAsync_NamedTripsThatCannotBeRepublished_AreSkippedAndReportedWhileTheOthersAreSent`
- [x] AC-4: A trip with no taken seats sends nothing — `HandleAsync_TripWithNoTakenSeats_SendsNothingAndReportsZeroEvents`
- [x] AC-5: A real change during the run wins — `HandleAsync_SeatLastChangedEarlierThanTheRun_OccurredAtIsTheChangeTimeNotTheRunTime`
  (the event is stamped with the seats' last change, so a later real change always outranks it; the subscriber's
  keep-the-latest rule is Search's)
- [x] AC-6: Nothing else is sent or changed — `HandleAsync_Run_ChangesNoSeatAndSendsOnlyHeldBookedAndBlockedEvents`
  (the job only reads and queries with consistent reads, so holds, confirms and blocks are never delayed)
- [x] AC-7: Repeat safety — `HandleAsync_RunTwice_SendsTheSameSeatsAndTimesWithNewEventIds`,
  `HandleAsync_SameCorrelationIdTwice_SendsEventsWithTheSameEventIds`
- [x] AC-8: Delivery failure — `HandleAsync_MessagingServiceDown_ReportsTheTripFailedAndTheRunFailed`,
  `HandleAsync_OneTripCannotBeRead_StillRepublishesTheOthersAndEndsAsFailed`
- Also: the `republished` field is absent on a real change — `HandleAsync_RealSeatChange_PublishesEventWithoutTheRepublishedField`
  (`PublishSeatChanges.Tests.cs`)

## Invariants touched

- Read only: one strongly consistent `Query` per trip (the trip record and its seats); `--republish-all` first runs one
  `Scan` for trip records, because the table has no index on departure. Nothing is written, so no seat changes and
  nothing reaches the seat-change stream.
- One `SeatHeld` per booking holding seats, one `SeatBooked` per booking owning seats, one `SeatBlocked` for every
  operator-blocked seat. Available seats, trip-cancellation blocks and every other event type are never sent.
- `occurredAt` is the latest `lastChangedAt` among the event's seats. `correlationId` is the run's. `eventId` is derived
  from the run, the trip, the event type and the booking, so a send retried within a run keeps its identity and the next
  run's events are new.
- Each event send is tried up to 3 times (1 s, then 2 s apart); a trip that still fails is reported failed and the run
  carries on with the others.
- The event types, envelope and the optional `republished` field are the ones `PublishSeatChanges` uses
  (`Common/Messaging/SeatEventMessages.cs`).

## Notes

- `check_structure.py` reports SL3 (no controller) for this slice: the API slice checker has no worker kind.
- Search parks seat events for trips it does not know, so Fleet must re-announce its trips before this job helps.
