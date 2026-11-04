# CAP-HOLD-005 — Create seat change

- Capability: CAP-HOLD-005
- Canonical spec: `capabilities/holds/features/seat-change.md` in `spec-RouteGo.SeatInventory.API` (version 0.1.0)
- Area: Holds
- Slice: `Features/Holds/CreateSeatChange/`

## Contract
Implemented **verbatim** from the issued contract — if it looks wrong, report drift upstream
(`skills-spec-authoring`), never deviate locally.

- Operation: `createSeatChange` — `contracts/holds/openapi.yaml` → `contracts/holds/seat-change.yaml`
- Route / verb: `POST /internal/trips/{tripId}/holds/{bookingId}/seatChanges`
- Credential: `InternalKey` only (`X-Internal-Key`) — an access token is never accepted here
- Request: `SeatChangeRequest` (`changes`: 1–6 `SeatMove`s)
- Response: `201` `SeatChange` (`200` when every move was already done); `changes` echoes the
  request's moves, `seatNos` is the booking's seats after the change
- Problem codes: `VALIDATION_FAILED` (400), `UNAUTHORIZED` (401), `NOT_FOUND` (404),
  `SEATS_NOT_OWNED` (409, `seatNos`), `SEATS_UNAVAILABLE` (409, `unavailableSeats`),
  `TRIP_CANCELLED` (409), `TRIP_DEPARTED` (409, `departedAt`), `UNKNOWN_SEATS` (422, `unknownSeats`),
  `SEAT_CLASS_MISMATCH` (422, `changes`)

## Acceptance criteria
Each criterion maps to tests in `CreateSeatChange.Tests.cs`; the request-shape, identifier,
unknown-trip and credential tests of AC-8 are in `CreateSeatChange.Request.Tests.cs`.

- [x] AC-1: Move to a free seat of the same class — `CreateSeatChange_FreeSeatOfSameClass_MovesTheBookingAndRecordsTheMove`
  (asserts the seat change record the seat-change stream turns into *SeatChanged* — publishing is CAP-SEAT-008)
- [x] AC-2: All or nothing — `CreateSeatChange_OneNewSeatTaken_RefusedSeatsUnavailableAndNothingMoves`
- [x] AC-3: Different class is refused — `CreateSeatChange_DifferentClass_RefusedSeatClassMismatchNamingTheMove`
- [x] AC-4: Only seats the booking owns — `CreateSeatChange_SeatBookedByAnotherBooking_RefusedSeatsNotOwned`,
  `CreateSeatChange_SeatOnlyHeldByTheBooking_RefusedSeatsNotOwnedAndNothingChanges`
- [x] AC-5: Repeat safety — `CreateSeatChange_SameChangeAgain_SucceedsOkAndChangesNothing`,
  `CreateSeatChange_RepeatWithOneMoreMove_MakesOnlyTheNewMove`
- [x] AC-6: Part of the booking moves — `CreateSeatChange_PartOfTheBooking_MovesOnlyThoseSeats`
- [x] AC-7: Not on a cancelled or departed trip — `CreateSeatChange_CancelledTrip_RefusedTripCancelled`,
  `CreateSeatChange_DepartedTrip_RefusedTripDeparted`
- [x] AC-8: Request rules, unknown seats, only Booking — `CreateSeatChange_MalformedMoves_RefusedValidationFailedNamingTheField`,
  `CreateSeatChange_MalformedIdentifier_RefusedValidationFailedNamingIt`, `CreateSeatChange_UnknownSeat_RefusedUnknownSeatsNamingIt`,
  `CreateSeatChange_UnknownTrip_RefusedNotFound`, `CreateSeatChange_NoKeyOrWrongKey_RefusedUnauthorizedAndNothingChanges`

Decisions agreed with the product owner (not spelled out in the spec):

- **When a move counts as done** (repeat safety): the to-seat is Booked by the booking **and** the
  from-seat no longer is. A move to a seat the booking already owns while it still owns the
  from-seat is not done — it is refused with `SEATS_UNAVAILABLE` naming the to-seat
  (`CreateSeatChange_ToASeatTheBookingAlsoOwns_RefusedSeatsUnavailable`).
- **Order of refusals** when several apply: trip (not found, cancelled, departed) → `UNKNOWN_SEATS`
  → `SEATS_NOT_OWNED` → `SEAT_CLASS_MISMATCH` → `SEATS_UNAVAILABLE`. Done moves are left out
  before the last three checks; a partial repeat applies only the moves still to do (201).

Known gap, left on purpose: a missing or malformed JSON body is refused by the framework with a
400 that carries no `code` (same as every slice with a body).

## Invariants touched
Business rules live in `Domain/`, never in this slice. List the ones this feature relies on.

- A seat change's shape: `SeatChangeRules` (1–6 moves, no seat named twice); `SeatMove`.
- No seat is ever sold twice: one `TransactWriteItems`; per from-seat `status = Booked AND bookingId = <booking>`,
  per to-seat `status = Available`, plus a `ConditionCheck` that the trip is not cancelled.
- A lost race re-reads and decides again (at most 3 attempts, then 500 — nothing changed and a
  seat change is safe to repeat).

verb-justified: the POST does write — a DynamoDB `TransactWriteItemsAsync` in `CreateSeatChange.Handler.cs`; the structure checker recognises only SQL and Postgres writer connections, so it cannot see it.

## Seat change record written (read by the seat-change stream, CAP-SEAT-008)

On every seat of a move still to do, `lastChangeReason = SeatChanged`, `lastChangeRequest` (one new
UUID shared by all seats of the request), `lastChangeCorrelationId`, `lastChangedAt`, and:

- **from-seat:** `status = Available`; removes `bookingId`, `holdExpiresAt`, `blockReason`,
  `lastChangedBy`, `lastChangeNote`, `lastChangeMovedFrom`.
- **to-seat:** `status = Booked`, `bookingId`, **`lastChangeMovedFrom` = the from-seat's label**;
  removes `holdExpiresAt`, `blockReason`, `lastChangedBy`, `lastChangeNote`.

The worker builds *SeatChanged*'s `changes` from the to-seats: each to-seat with reason
`SeatChanged` gives one move `lastChangeMovedFrom → seatNo`. `lastChangeMovedFrom` is meaningful
only when `lastChangeReason = SeatChanged` (other slices do not clear it).

**Upstream follow-up:** `lastChangeMovedFrom` is a new TripSeat attribute agreed with the product
owner; `data-model/trip-seats/trip-seat.md` must gain it.
