# CAP-SEAT-002 — List seat allocations

- Capability: CAP-SEAT-002
- Canonical spec: `capabilities/trip-seats/features/seat-allocations.md` in `spec-RouteGo.SeatInventory.API` (version 0.1.0)
- Area: TripSeats
- Slice: `Features/TripSeats/ListSeatAllocations/`

## Contract
Implemented **verbatim** from the issued contract — if it looks wrong, report drift upstream
(`skills-spec-authoring`), never deviate locally.

- Operation: `listSeatAllocations` — `contracts/trip-seats/openapi.yaml` → `contracts/trip-seats/seat-allocation.yaml`
- Route / verb: `GET /trips/{tripId}/seatAllocations`
- Request: path parameter `tripId` (UUID); `BearerAuth` access token, role Admin or Operator
- Response: `200` with `SeatAllocationList` (`tripId`, `operatorId`, `departureAt`, `isCancelled`, `isDeparted`, `items[]` of `SeatAllocation`)
- Problem codes: `VALIDATION_FAILED` (400), `UNAUTHORIZED` (401), `FORBIDDEN` (403), `NOT_FOUND` (404)

## Acceptance criteria
Each criterion maps to one test in `ListSeatAllocations.Tests.cs`.

- [x] AC-1: An admin sees who holds each seat — `ListSeatAllocations_Admin_SeesHolderExpiryAndBlockReasonOfEachSeat`
- [x] AC-2: The operator of the trip sees its own trip — `ListSeatAllocations_OperatorOfTheTrip_SeesTheSameAsAnAdmin`
- [x] AC-3: A cancelled trip's blocks say so — `ListSeatAllocations_CancelledTrip_BlocksSayTripCancelled`
- [x] AC-4: Another operator's trip — `ListSeatAllocations_AnotherOperatorsTrip_RefusedForbidden`
- [x] AC-5: Only admins and operators, signed in — `ListSeatAllocations_Customer_RefusedForbidden`,
  `ListSeatAllocations_NoAccessToken_RefusedUnauthorized`, `ListSeatAllocations_TokenSignedByUntrustedKey_RefusedUnauthorized`
- [x] AC-6: Unknown trip — `ListSeatAllocations_UnknownTrip_RefusedNotFound`, `ListSeatAllocations_TripIdNotAUuid_RefusedValidationFailed`
- Business rule 4 (an expired hold the timer has not ended is still Held) — `ListSeatAllocations_HoldExpiryPassedBeforeTimerRan_StillShownHeldWithItsExpiry`

## Invariants touched
Business rules live in `Domain/`, never in this slice. List the ones this feature relies on.

- An operator acts only on its own trips: the trip's `operatorId` (from *TripScheduled*) must equal the token's `operatorId`.
- A trip is **departed** once its departure time has passed; derived per request from `IClock`.

## Notes
- Another operator's trip answers **403 FORBIDDEN**, as the contract and AC-4 state, even though
  the coding standard's default is 404 for out-of-scope resources — the contract wins.
- Reads the seat store layout described in `Features/TripSeats/GetSeatMap/SPEC.md`, plus the
  trip's `operatorId` and the seat's `bookingId`, `holdExpiresAt` (ISO-8601 UTC) and
  `blockReason` (`Operator` / `TripCancelled`).
