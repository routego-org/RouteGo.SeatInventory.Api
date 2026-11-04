# CAP-HOLD-001 — Create hold

- Capability: CAP-HOLD-001
- Canonical spec: `capabilities/holds/features/seat-hold.md` in `spec-RouteGo.SeatInventory.API` (version 0.1.1)
- Area: Holds
- Slice: `Features/Holds/CreateHold/`

## Contract
Implemented **verbatim** from the issued contract — if it looks wrong, report drift upstream
(`skills-spec-authoring`), never deviate locally.

- Operation: `createHold` — `contracts/holds/openapi.yaml` → `contracts/holds/hold.yaml`
- Route / verb: `POST /internal/trips/{tripId}/holds`
- Request: `HoldRequest` (`bookingId` UUID, `seatNos` 1–6 different labels, optional `holdSeconds` ≥ 1);
  `InternalKey` only (`X-Internal-Key`) — an access token is never accepted here
- Response: `201` `Hold` with `Location: /internal/trips/{tripId}/holds/{bookingId}`; `200` `Hold`
  when the booking already holds (or has booked) exactly these seats
- Problem codes: `VALIDATION_FAILED` (400), `UNAUTHORIZED` (401), `NOT_FOUND` (404),
  `SEATS_UNAVAILABLE` (409, `unavailableSeats`), `BOOKING_ALREADY_HAS_SEATS` (409, `seatNos`, `holdStatus`),
  `TRIP_CANCELLED` (409), `TRIP_DEPARTED` (409, `departedAt`), `UNKNOWN_SEATS` (422, `unknownSeats`),
  `HOLD_TOO_LONG` (422, `maxHoldSeconds`)

## Acceptance criteria
Each criterion maps to tests in `CreateHold.Tests.cs`.

- [x] AC-1: Hold free seats — `CreateHold_FreeSeatsWithoutLength_HoldsThemForTenMinutesAndStartsOneTimer`
  (asserts the seat change record the seat-change stream turns into *SeatHeld* — publishing is CAP-SEAT-008 —
  and the one timer: delay 600 s, the stored expiry, the seats, the correlation ID);
  `CreateHold_OneSecondHold_TimerArrivesOnTheQueueAtItsExpiry` (the timer really comes off the queue)
- [x] AC-2: 100 requests for one seat, exactly one wins — `CreateHold_HundredRequestsForOneSeatAtOnce_ExactlyOneIsHeld`
- [x] AC-3: All or nothing — `CreateHold_OneSeatHeldByAnotherBooking_HoldsNothingAndStartsNoTimer`
- [x] AC-4: Every unavailable seat is named — `CreateHold_BookedAndBlockedSeats_NamesEveryUnavailableSeat`
- [x] AC-5: Chosen hold length — `CreateHold_FifteenSecondLength_ExpiresFifteenSecondsLater`
- [x] AC-6: Above the ceiling is refused — `CreateHold_LengthAboveCeiling_RefusedHoldTooLongAndNothingHeld`;
  a length of 0 — `CreateHold_MalformedRequest_RefusedValidationFailedNamingTheField` (holdSeconds case)
- [x] AC-7: Repeat safety — `CreateHold_SameRequestRepeatedInAnotherOrder_ReturnsTheExistingHoldUnchanged`;
  the same seats already Booked (product owner decision 2026-10-20: 200 with status `booked`) —
  `CreateHold_SameSeatsAlreadyBooked_ReturnsTheBookedHold`
- [x] AC-8: One hold per booking — `CreateHold_BookingHoldsOtherSeats_RefusedBookingAlreadyHasSeats`,
  `CreateHold_BookingOwnsOtherBookedSeats_RefusedBookingAlreadyHasSeats`
- [x] AC-9: No hold after departure — `CreateHold_TwentyMinutesAfterDeparture_RefusedTripDepartedWithDepartureTime`,
  `CreateHold_TwoMinutesBeforeDeparture_IsHeld`
- [x] AC-10: No hold on a cancelled trip — `CreateHold_CancelledTrip_RefusedTripCancelled`
- [x] AC-11: Request rules, unknown trip and seats — `CreateHold_MalformedRequest_RefusedValidationFailedNamingTheField`
  (no seats, 7 seats, a seat twice, no booking, a booking that is not a UUID),
  `CreateHold_SeatTheTripDoesNotHave_RefusedUnknownSeats`, `CreateHold_UnknownTrip_RefusedNotFound`
- [x] AC-12: Only Booking may hold — `CreateHold_NoKeyWrongKeyOrOnlyAnAccessToken_RefusedUnauthorizedAndNothingChanges`
- Timer first (decision 2026-10-20): `CreateHold_TimerCannotBeStarted_FailsAndHoldsNothing`
- Correlation (service rule 8): `CreateHold_NoCorrelationIdSent_OneIsMadeForTheSeatsAndTheTimer`
- AC-13 to AC-21 belong to CAP-HOLD-002 (`ConfirmHold`) and CAP-HOLD-003 (`ReleaseHold`).

## Invariants touched
Business rules live in `Domain/`, never in this slice. List the ones this feature relies on.

- `SeatHoldRules`: 1–6 different seats; length ≥ 1 s, 600 s by default. The ceiling is
  `MaxHoldSeconds` (optional env, default and maximum 900 — the queue's longest delay;
  `SeatHoldRules.ToCeilingSeconds`, checked at startup).
- No seat is ever sold twice: one `TransactWriteItems` with a `ConditionCheck` that the trip is not
  cancelled and, per seat, `status = Available`. A lost race (condition failed, or a conflicting
  transaction) re-reads the trip and decides again — so a simultaneous repeat of the same request
  gets the hold, not a refusal — with at most 3 write attempts.
- The timer is sent **before** the seats change: if it cannot be sent nothing is held (500, Booking
  retries); a timer whose hold then lost a race is harmless, because the expiry worker frees only
  seats Held by that booking with that exact `holdExpiresAt`.

verb-justified: the POST does write — a DynamoDB `TransactWriteItemsAsync` in `CreateHold.Handler.cs` and an SQS `SendMessageAsync`; the structure checker recognises only SQL and Postgres writer connections, so it cannot see them.

## Seat change record written (read by the seat-change stream, CAP-SEAT-008)

On every seat this request holds: `status = Held`, `bookingId`, `holdExpiresAt` (ISO-8601 UTC, the
same text the timer carries), `lastChangeReason = Held`, `lastChangeRequest` (one new UUID shared by
all seats of the request), `lastChangeCorrelationId` (the `X-Correlation-Id` header, or a new UUID
when absent or longer than 128 characters), `lastChangedAt`; removes `blockReason`, `lastChangedBy`,
`lastChangeNote` (Booking is not a staff user).

## Hold timer (read by the hold-expiry worker, CAP-HOLD-006)

One SQS message per **new** hold on `HoldExpiryQueueUrl`, `DelaySeconds` = the hold length, body
(camelCase JSON): `tripId`, `bookingId`, `holdExpiresAt` (exactly as stored on the seats), `seatNos`,
`correlationId`. A repeat starts none.

## Known gaps

- A body that is not valid JSON gets the framework's automatic 400 without `code: VALIDATION_FAILED`
  (same for every slice; fixing it needs a `Program.cs` change — decided 2026-10-20 to leave it).
- "One hold per booking" is checked on the read before the write; two simultaneous requests for the
  same booking but *different* free seats could both succeed. Booking sends one hold per booking at
  a time; an atomic guard would need a per-booking item in the table.
