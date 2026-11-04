# CAP-HOLD-003 — Release hold

- Capability: CAP-HOLD-003
- Canonical spec: `capabilities/holds/features/seat-hold.md` in `spec-RouteGo.SeatInventory.API` (version 0.1.1)
- Area: Holds
- Slice: `Features/Holds/ReleaseHold/`

## Contract
Implemented **verbatim** from the issued contract — if it looks wrong, report drift upstream
(`skills-spec-authoring`), never deviate locally.

- Operation: `releaseHold` — `contracts/holds/openapi.yaml` → `contracts/holds/hold.yaml`
- Route / verb: `POST /internal/trips/{tripId}/holds/{bookingId}/release` (no body)
- Credential: `InternalKey` only (`X-Internal-Key`) — an access token is never accepted here
- Response: `200` `HoldRelease` (`releasedSeatNos`, empty when nothing was held)
- Problem codes: `VALIDATION_FAILED` (400), `UNAUTHORIZED` (401), `NOT_FOUND` (404),
  `HOLD_ALREADY_CONFIRMED` (409, `seatNos`)

## Acceptance criteria
Each criterion maps to tests in `ReleaseHold.Tests.cs`.

- [x] AC-18: Release a Held hold at once — `ReleaseHold_HeldSeats_MakesThemAvailableTogether`
  (asserts the seat change record the seat-change stream turns into *SeatReleased*, reason
  `HoldReleased`, released to Available — publishing is CAP-SEAT-008. "The timer later changes
  nothing" holds because the seats are no longer Held by the booking; the timer is CAP-HOLD-006)
- [x] AC-19: Releasing a hold that is gone changes nothing — `ReleaseHold_ReleasedTwice_SecondNamesNoSeatsAndChangesNothing`,
  `ReleaseHold_BookingNeverHeldAnything_SucceedsNamingNoSeats`
- [x] AC-20: A confirmed hold cannot be released — `ReleaseHold_SeatsAlreadyBooked_RefusedHoldAlreadyConfirmedAndStaysBooked`
- [x] AC-21: Release on a cancelled trip blocks the seats — `ReleaseHold_CancelledTrip_BlocksTheSeatsInsteadOfFreeingThem`
- Only this booking's seats change — `ReleaseHold_AnotherBookingsSeat_IsLeftAlone`
- Unknown trip, malformed identifiers, credentials — `ReleaseHold_UnknownTrip_RefusedNotFound`,
  `ReleaseHold_MalformedIdentifier_RefusedValidationFailedNamingIt`,
  `ReleaseHold_NoKeyWrongKeyOrOnlyAnAccessToken_RefusedUnauthorizedAndNothingChanges`

## Invariants touched
Business rules live in `Domain/`, never in this slice. List the ones this feature relies on.

- No seat is ever sold twice: one `TransactWriteItems`; per seat `status = Held AND bookingId = <booking>`.
- A cancelled trip's seats never return to sale (ADR 0007): released seats go to Blocked
  (`blockReason = TripCancelled`) on a cancelled trip. A release to Available also carries a
  `ConditionCheck` that the trip is still not cancelled, so a cancellation landing between the
  read and the write fails the transaction and the next attempt blocks the seats instead.
- A lost race re-reads and decides again (at most 3 attempts, then 500 — nothing changed and a
  release is safe to repeat).

verb-justified: the POST does write — a DynamoDB `TransactWriteItemsAsync` in `ReleaseHold.Handler.cs`; the structure checker recognises only SQL and Postgres writer connections, so it cannot see it.

## Seat change record written (read by the seat-change stream, CAP-SEAT-008)

On every seat this request releases: `status = Available` (or `Blocked` + `blockReason = TripCancelled`
on a cancelled trip), `lastChangeReason = HoldReleased`, `lastChangeRequest` (one new UUID shared
by all seats of the request), `lastChangeCorrelationId`, `lastChangedAt`; removes `bookingId`,
`holdExpiresAt`, `lastChangedBy`, `lastChangeNote`. The released booking is in the change's old image.
