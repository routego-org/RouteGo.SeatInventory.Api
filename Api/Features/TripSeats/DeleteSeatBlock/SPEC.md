# CAP-SEAT-004 — Delete seat block

- Capability: CAP-SEAT-004
- Canonical spec: `capabilities/trip-seats/features/operator-seat-blocking.md` in `spec-RouteGo.SeatInventory.API` (version 0.1.0)
- Area: TripSeats
- Slice: `Features/TripSeats/DeleteSeatBlock/`

## Contract
Implemented **verbatim** from the issued contract — if it looks wrong, report drift upstream
(`skills-spec-authoring`), never deviate locally.

- Operation: `deleteSeatBlock` — `contracts/trip-seats/openapi.yaml` → `contracts/trip-seats/seat-block.yaml`
- Route / verb: `DELETE /trips/{tripId}/seatBlocks?seatNos=L1,L2` (one comma-separated value, `style: form, explode: false`)
- Credential: `BearerAuth`, role Operator, the trip's own operator
- Response: `204` (no body) — also when every seat was already Available
- Problem codes: `VALIDATION_FAILED` (400), `UNAUTHORIZED` (401), `FORBIDDEN` (403), `NOT_FOUND` (404),
  `SEATS_NOT_OPERATOR_BLOCKED` (409, `seatNos`), `TRIP_CANCELLED` (409), `TRIP_DEPARTED` (409, `departedAt`),
  `UNKNOWN_SEATS` (422, `unknownSeats`)

## Acceptance criteria
Each criterion maps to tests in `DeleteSeatBlock.Tests.cs`.

- [x] AC-5: Unblock seats the operator blocked — `DeleteSeatBlock_OperatorBlockedSeats_MakesThemAvailableAndRecordsTheChange`
  (asserts the seat change record the seat-change stream turns into *SeatUnblocked* and the audit
  record — reason `OperatorUnblocked`, shared request id, `lastChangedBy`; publishing is CAP-SEAT-008)
- [x] AC-6: Repeat safety — `DeleteSeatBlock_SeatAlreadyAvailable_SucceedsWithNoChange`
- [x] AC-7: Only the operator's own blocks — `DeleteSeatBlock_BookedSeat_RefusedSeatsNotOperatorBlockedAndNothingChanges`,
  `DeleteSeatBlock_SeatBlockedByCancellation_RefusedSeatsNotOperatorBlocked`
- [x] AC-8: A cancelled trip's seats can never be unblocked — `DeleteSeatBlock_CancelledTrip_RefusedTripCancelledAndSeatsStayBlocked`
- [x] AC-9 (unblock part): departed trip — `DeleteSeatBlock_DepartedTrip_RefusedTripDepartedWithDepartureTime`
- [x] AC-10 (unblock part): unknown seats — `DeleteSeatBlock_SeatTheTripDoesNotHave_RefusedUnknownSeatsAndNothingChanges`
- [x] AC-11 (unblock part): request rules — `DeleteSeatBlock_MissingEmptyDuplicateBlankOrOverSixtySeats_RefusedValidationFailedOnSeatNos`,
  `DeleteSeatBlock_MalformedTripId_RefusedValidationFailedOnTripId`
- [x] AC-12 (unblock part): only the trip's own operator — `DeleteSeatBlock_AnotherOperatorsTrip_RefusedForbiddenAndNothingChanges`,
  `DeleteSeatBlock_AdminOrCustomer_RefusedForbidden`, `DeleteSeatBlock_NoAccessToken_RefusedUnauthorized`,
  `DeleteSeatBlock_UnknownTrip_RefusedNotFound`

Order of refusals (same as the block slice): not found → another operator's trip (`FORBIDDEN`) →
`TRIP_CANCELLED` → `TRIP_DEPARTED` → `UNKNOWN_SEATS` → `SEATS_NOT_OPERATOR_BLOCKED`.

## Invariants touched
Business rules live in `Domain/`, never in this slice. List the ones this feature relies on.

- The seat list: `SeatBlockRules.IsValidSeatList` (1–60 different, non-blank labels) — shared with the block slice.
- A cancellation block is never lifted (ADR 0007): a seat with `blockReason = TripCancelled` is refused,
  every seat write is conditional on `status = Blocked AND blockReason = Operator`, and the transaction
  carries a `ConditionCheck` that the trip is not cancelled — a cancellation landing between the read
  and the write fails it, and the next attempt answers `TRIP_CANCELLED`.
- A lost race re-reads and decides again (at most 3 attempts, then 500 — nothing changed and an
  unblock is safe to repeat).

verb-justified: the DELETE does write — a DynamoDB `TransactWriteItemsAsync` in `DeleteSeatBlock.Handler.cs`; the structure checker recognises only SQL and Postgres writer connections, so it cannot see it.

## Seat change record written (read by the seat-change stream, CAP-SEAT-008)

On every seat this request unblocks: `status = Available`, `lastChangeReason = OperatorUnblocked`,
`lastChangeRequest` (one new UUID shared by all seats of the request), `lastChangeCorrelationId`,
`lastChangedBy` (the token's `sub`), `lastChangedAt`; removes `blockReason`, `bookingId`,
`holdExpiresAt`, `lastChangeNote`, `lastChangeMovedFrom`. Seats already Available are not written,
so a request that changes nothing produces no event and no audit record. The audit record's role
(Operator) follows from the reason, as for the block slice.
