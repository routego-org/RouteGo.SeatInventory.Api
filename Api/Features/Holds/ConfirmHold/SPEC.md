# CAP-HOLD-002 — Confirm hold

- Capability: CAP-HOLD-002
- Canonical spec: `capabilities/holds/features/seat-hold.md` in `spec-RouteGo.SeatInventory.API` (version 0.1.1)
- Area: Holds
- Slice: `Features/Holds/ConfirmHold/`

## Contract
Implemented **verbatim** from the issued contract — if it looks wrong, report drift upstream
(`skills-spec-authoring`), never deviate locally.

- Operation: `confirmHold` — `contracts/holds/openapi.yaml` → `contracts/holds/hold.yaml`
- Route / verb: `POST /internal/trips/{tripId}/holds/{bookingId}/confirm` (no body)
- Credential: `InternalKey` only (`X-Internal-Key`) — an access token is never accepted here
- Response: `200` `Hold` (`status: booked`, `holdExpiresAt: null`)
- Problem codes: `VALIDATION_FAILED` (400), `UNAUTHORIZED` (401), `NOT_FOUND` (404),
  `HOLD_NOT_ACTIVE` (409), `TRIP_CANCELLED` (409)

## Acceptance criteria
Each criterion maps to tests in `ConfirmHold.Tests.cs`.

- [x] AC-13: Confirm a paid hold — `ConfirmHold_HeldSeats_BooksThemTogetherWithNoExpiry`
  (asserts the seat change record the seat-change stream turns into *SeatBooked* — publishing is
  CAP-SEAT-008. "The timer later changes nothing" holds because the seats are no longer Held; the
  timer itself is CAP-HOLD-006, worker project)
- [x] AC-14: Confirm three times, same result — `ConfirmHold_ConfirmedThreeTimes_SameAnswerAndOneChange`
  (one change record, so one *SeatBooked*)
- [x] AC-15: A late confirm before the timer has run — `ConfirmHold_AfterTheExpiryBeforeTheTimerRan_BooksTheSeats`
- [x] AC-16: Confirm after release is refused — `ConfirmHold_HoldReleasedAndSeatNowHeldByAnother_RefusedHoldNotActiveAndSeatUntouched`,
  `ConfirmHold_BookingNeverHeldAnything_RefusedHoldNotActive`
- [x] AC-17: Confirm on a cancelled trip — `ConfirmHold_CancelledTrip_RefusedTripCancelledAndSeatStaysHeld`
- Business rule 7 (never refused for departure) — `ConfirmHold_TripAlreadyDeparted_StillBooksTheSeats`
- Unknown trip, malformed identifiers, credentials — `ConfirmHold_UnknownTrip_RefusedNotFound`,
  `ConfirmHold_MalformedIdentifier_RefusedValidationFailedNamingIt`,
  `ConfirmHold_NoKeyWrongKeyOrOnlyAnAccessToken_RefusedUnauthorizedAndNothingChanges`

## Invariants touched
Business rules live in `Domain/`, never in this slice. List the ones this feature relies on.

- No seat is ever sold twice: one `TransactWriteItems` with a `ConditionCheck` that the trip is not
  cancelled and, per Held seat, `status = Held AND bookingId = <booking>`. A confirm racing the hold
  timer or a release either books every seat or none; a lost race re-reads and decides again (at
  most 3 attempts, then 500 — nothing changed and a confirm is safe to repeat).
- Repeat safety: seats already Booked by the booking count as confirmed and are not written again.

verb-justified: the POST does write — a DynamoDB `TransactWriteItemsAsync` in `ConfirmHold.Handler.cs`; the structure checker recognises only SQL and Postgres writer connections, so it cannot see it.

## Seat change record written (read by the seat-change stream, CAP-SEAT-008)

On every seat this request books: `status = Booked`, `lastChangeReason = Booked`,
`lastChangeRequest` (one new UUID shared by all seats of the request), `lastChangeCorrelationId`
(`X-Correlation-Id`, or a new UUID), `lastChangedAt`; removes `holdExpiresAt`, `blockReason`,
`lastChangedBy`, `lastChangeNote`. `bookingId` stays.
