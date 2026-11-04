# CAP-HOLD-004 — Cancel hold

- Capability: CAP-HOLD-004
- Canonical spec: `capabilities/holds/features/admin-hold-cancellation.md` in `spec-RouteGo.SeatInventory.API` (version 0.1.0)
- Area: Holds
- Slice: `Features/Holds/CancelHold/`

## Contract
Implemented **verbatim** from the issued contract — if it looks wrong, report drift upstream
(`skills-spec-authoring`), never deviate locally.

- Operation: `cancelHold` — `contracts/holds/openapi.yaml` → `contracts/holds/hold.yaml`
- Route / verb: `POST /trips/{tripId}/holds/{bookingId}/cancel`
- Credential: `BearerAuth`, role Admin, any trip
- Request: `HoldCancellation` (`note`: 10–500 characters)
- Response: `200` `HoldRelease` (`releasedSeatNos`)
- Problem codes: `VALIDATION_FAILED` (400), `UNAUTHORIZED` (401), `FORBIDDEN` (403), `NOT_FOUND` (404),
  `HOLD_NOT_ACTIVE` (409), `HOLD_ALREADY_CONFIRMED` (409, `seatNos`)

## Acceptance criteria
Each criterion maps to tests in `CancelHold.Tests.cs`.

- [x] AC-1: Cancel a stuck hold — `CancelHold_StuckHold_MakesSeatsAvailableAndRecordsTheAdminAndNote`
  (asserts the seat change record the seat-change stream turns into *SeatReleased* — reason
  `AdminReleased`, released to Available — and *AuditRecorded*: `lastChangedBy` is the admin's
  `sub`, `lastChangeNote` the note. Publishing is CAP-SEAT-008)
- [x] AC-2: A paid hold cannot be cancelled here — `CancelHold_SeatsBooked_RefusedHoldAlreadyConfirmedAndStaysBooked`
- [x] AC-3: Nothing to cancel — `CancelHold_BookingHoldsNothing_RefusedHoldNotActive`,
  `CancelHold_CancelledTwice_SecondRefusedHoldNotActiveAndChangesNothing`
- [x] AC-4: On a cancelled trip the seats are blocked — `CancelHold_CancelledTrip_BlocksTheSeatsInsteadOfFreeingThem`
- [x] AC-5: The note is required — `CancelHold_MissingShortLongOrBlankNote_RefusedValidationFailedNamingNoteAndNothingChanges`
  (a note of spaces only is also refused, even at 10+ characters — agreed with the product owner)
- [x] AC-6: Only admins — `CancelHold_OperatorOrCustomer_RefusedForbiddenAndNothingChanges`,
  `CancelHold_NoAccessToken_RefusedUnauthorizedAndNothingChanges`, `CancelHold_UnknownTrip_RefusedNotFound`
- Only this booking's seats change — `CancelHold_AnotherBookingsSeat_IsLeftAlone`
- Malformed identifiers — `CancelHold_MalformedIdentifier_RefusedValidationFailedNamingIt`

Known gap, left on purpose: a missing or malformed JSON body is refused by the framework with a
400 that carries no `code` (same as every slice with a body).

## Invariants touched
Business rules live in `Domain/`, never in this slice. List the ones this feature relies on.

- The admin's note: `HoldCancellationRules.IsValidNote` (10–500 characters, not only spaces).
- No seat is ever sold twice: one `TransactWriteItems`; per seat `status = Held AND bookingId = <booking>`.
- A cancelled trip's seats never return to sale (ADR 0007): released seats go to Blocked
  (`blockReason = TripCancelled`) on a cancelled trip. A release to Available also carries a
  `ConditionCheck` that the trip is still not cancelled, so a cancellation landing between the
  read and the write fails the transaction and the next attempt blocks the seats instead.
- A lost race re-reads and decides again (at most 3 attempts, then 500 — nothing changed). If the
  winner was the hold timer, the next attempt finds nothing held and answers `HOLD_NOT_ACTIVE`.

verb-justified: the POST does write — a DynamoDB `TransactWriteItemsAsync` in `CancelHold.Handler.cs`; the structure checker recognises only SQL and Postgres writer connections, so it cannot see it.

## Seat change record written (read by the seat-change stream, CAP-SEAT-008)

On every seat this request releases: `status = Available` (or `Blocked` + `blockReason = TripCancelled`
on a cancelled trip), `lastChangeReason = AdminReleased`, `lastChangeRequest` (one new UUID shared
by all seats of the request), `lastChangeCorrelationId`, `lastChangedBy` (the admin's `sub`),
`lastChangeNote` (the note), `lastChangedAt`; removes `bookingId`, `holdExpiresAt`. The cancelled
booking is in the change's old image.

The audit record's `actorRole` is not stored on the seat: reason `AdminReleased` always means role
Admin, so the stream worker derives it from the reason — as it derives Operator from `OperatorBlocked`
(agreed with the product owner; no data-model change).
