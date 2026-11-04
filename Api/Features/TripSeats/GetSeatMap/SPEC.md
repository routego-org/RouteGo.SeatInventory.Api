# CAP-SEAT-001 — Get seat map

- Capability: CAP-SEAT-001
- Canonical spec: `capabilities/trip-seats/features/seat-map.md` in `spec-RouteGo.SeatInventory.API` (version 0.1.0)
- Area: TripSeats
- Slice: `Features/TripSeats/GetSeatMap/`

## Contract
Implemented **verbatim** from the issued contract — if it looks wrong, report drift upstream
(`skills-spec-authoring`), never deviate locally.

- Operation: `getSeatMap` — `contracts/trip-seats/openapi.yaml` → `contracts/trip-seats/seat-map.yaml`
- Route / verb: `GET /trips/{tripId}/seats` (no route prefix — the contract's path as issued)
- Request: path parameter `tripId` (UUID); no body; no credential (`security: []`)
- Response: `200` with `SeatMap` (`tripId`, `departureAt`, `isCancelled`, `isDeparted`, `seats[]` of `MapSeat`)
- Problem codes: `VALIDATION_FAILED` (400, `tripId` not a UUID), `NOT_FOUND` (404)

## Acceptance criteria
Each criterion maps to one test in `GetSeatMap.Tests.cs`.

- [x] AC-1: Seat map of a scheduled trip — `GetSeatMap_ScheduledTrip_ListsSeatsWithPositionClassFareAndStatus`
- [x] AC-2: Never shows who holds or owns a seat — `GetSeatMap_SeatsHeldAndBooked_NoBookingOrHoldExpiryInAnswer`
- [x] AC-3: All four statuses are shown — `GetSeatMap_SeatInEachStatus_ShowsAvailableHeldBookedAndBlocked`
- [x] AC-4: The fare shown is today's fare — `GetSeatMap_FareChangedSinceCreation_ShowsTodaysFare`
- [x] AC-5: A cancelled trip still answers, and says so — `GetSeatMap_CancelledTrip_ListsSeatsAndReportsCancelled`
- [x] AC-6: A departed trip still answers, and says so — `GetSeatMap_DepartureTimePassed_ListsSeatsAndReportsDeparted`
- [x] AC-7: Unknown trip — `GetSeatMap_UnknownTrip_RefusedNotFound`, `GetSeatMap_TripIdNotAUuid_RefusedValidationFailed`
- [x] AC-8: Fast enough to draw at once — `GetSeatMap_SixtySeatTripAskedHundredTimes_AtLeastNinetyFiveWithinHalfASecond`
  (against DynamoDB Local; proves the code path is not slow — the deployed figure is confirmed by a load test on AWS)
- Business rule 3 (deck, then row, then column) — `GetSeatMap_SeatsStoredOutOfOrder_ListedByDeckThenRowThenColumn`

## Invariants touched
Business rules live in `Domain/`, never in this slice. List the ones this feature relies on.

- A trip is **departed** once its departure time has passed; derived per request from `IClock`, never stored.
- Held and booked seats look the same to every caller: the query projects only the public
  attributes, so booking identifiers and hold expiry never leave the store.

## Seat store layout (read here; written by the trip-seat-creation worker)
One DynamoDB table (name from `SeatStoreTableName`), keys `pk` / `sk`:

| Item | `pk` | `sk` | Attributes read here |
|---|---|---|---|
| Trip | `TRIP#<tripId>` | `TRIP` | `departureAt` (ISO-8601 UTC string), `isCancelled` (bool) |
| Seat | `TRIP#<tripId>` | `SEAT#<seatNo>` | `seatNo`, `deck` (`Lower`/`Upper`), `row`, `column`, `seatClass`, `fare`, `status` (`Available`/`Held`/`Booked`/`Blocked`) |

A trip item with no `departureAt` (known only from an early *TripCancelled*) is answered as
`NOT_FOUND` — Fleet has not announced it yet.
