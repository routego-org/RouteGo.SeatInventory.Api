# CAP-SEAT-003 — Create seat block

- Capability: CAP-SEAT-003
- Canonical spec: `capabilities/trip-seats/features/operator-seat-blocking.md` in `spec-RouteGo.SeatInventory.API` (version 0.1.0)
- Area: TripSeats
- Slice: `Features/TripSeats/CreateSeatBlock/`

## Contract
Implemented **verbatim** from the issued contract — if it looks wrong, report drift upstream
(`skills-spec-authoring`), never deviate locally.

- Operation: `createSeatBlock` — `contracts/trip-seats/openapi.yaml` → `contracts/trip-seats/seat-block.yaml`
- Route / verb: `POST /trips/{tripId}/seatBlocks`
- Request: `SeatBlockRequest` (`seatNos`: 1–60 different labels); `BearerAuth`, role Operator, the trip's own operator
- Response: `201` `SeatBlock` when at least one seat changed; `200` `SeatBlock` when nothing needed to change
- Problem codes: `VALIDATION_FAILED` (400), `UNAUTHORIZED` (401), `FORBIDDEN` (403), `NOT_FOUND` (404),
  `SEATS_UNAVAILABLE` (409, `unavailableSeats`), `TRIP_CANCELLED` (409), `TRIP_DEPARTED` (409, `departedAt`),
  `UNKNOWN_SEATS` (422, `unknownSeats`)

## Acceptance criteria
Each criterion maps to tests in `CreateSeatBlock.Tests.cs`.

- [x] AC-1: Block free seats — `CreateSeatBlock_FreeSeats_BlocksThemAndRecordsTheChange`
  (asserts the seat change record — reason `OperatorBlocked`, shared request id, `lastChangedBy` —
  that the seat-change stream turns into *SeatBlocked* and *AuditRecorded*; publishing is CAP-SEAT-008)
- [x] AC-2: All or nothing — `CreateSeatBlock_OneSeatHeld_RefusedSeatsUnavailableAndNothingChanges`
- [x] AC-3: A booked seat can never be blocked — `CreateSeatBlock_BookedSeat_RefusedSeatsUnavailableAndStaysBooked`
- [x] AC-4: Repeat safety — `CreateSeatBlock_OneSeatAlreadyBlockedByOperator_BlocksOnlyTheOther`,
  `CreateSeatBlock_SameRequestRepeated_SucceedsWithNoChange`
- [x] AC-9: No block on a cancelled or departed trip — `CreateSeatBlock_CancelledTrip_RefusedTripCancelled`,
  `CreateSeatBlock_DepartedTrip_RefusedTripDepartedWithDepartureTime`
- [x] AC-10: Seats the trip does not have — `CreateSeatBlock_SeatTheTripDoesNotHave_RefusedUnknownSeatsAndNothingChanges`
- [x] AC-11: Request rules — `CreateSeatBlock_EmptyDuplicateOrOverSixtySeats_RefusedValidationFailedOnSeatNos`
- [x] AC-12: Only the trip's own operator — `CreateSeatBlock_AnotherOperatorsTrip_RefusedForbidden`,
  `CreateSeatBlock_AdminOrCustomer_RefusedForbidden`, `CreateSeatBlock_NoAccessToken_RefusedUnauthorized`,
  `CreateSeatBlock_UnknownTrip_RefusedNotFound`
- Business rule 3 (a cancellation block is not free) — `CreateSeatBlock_SeatBlockedByCancellation_RefusedSeatsUnavailable`
- AC-5 to AC-8 belong to CAP-SEAT-004 (`Features/TripSeats/DeleteSeatBlock/`).

## Invariants touched
Business rules live in `Domain/`, never in this slice. List the ones this feature relies on.

- No seat is ever sold twice / blocking never takes a customer's seat: one `TransactWriteItems`
  with a `ConditionCheck` that the trip is not cancelled and, per seat, `status = Available`.
  A lost race maps to `TRIP_CANCELLED` or `SEATS_UNAVAILABLE` naming the seats that failed.
- Repeat safety: seats already blocked by the operator are not written again.

verb-justified: the POST does write — a DynamoDB `TransactWriteItemsAsync` in `CreateSeatBlock.Handler.cs`; the structure checker recognises only SQL and Postgres writer connections, so it cannot see it.

## Seat change record written (read by the seat-change stream, CAP-SEAT-008)

On every seat this request changes: `status = Blocked`, `blockReason = Operator`,
`lastChangeReason = OperatorBlocked`, `lastChangeRequest` (one new UUID shared by all seats of
the request), `lastChangeCorrelationId` (the request's `X-Correlation-Id`, or a new UUID),
`lastChangedBy` (the token's `sub`), `lastChangedAt` (ISO-8601 UTC); removes `bookingId`,
`holdExpiresAt`, `lastChangeNote`, `lastChangeMovedFrom`. The trip's `operatorId` is on the trip item.
