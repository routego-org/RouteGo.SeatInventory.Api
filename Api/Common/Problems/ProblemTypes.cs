namespace Api.Common.Problems;

/// <summary>
/// Projection of the product's problem-type catalog (contracts/problem-types.md in the
/// product-specs repo). Codes are never minted here — a new code starts life in the catalog
/// and lands here as a constant carrying the same code and the same type URI, so this class
/// can be diffed against the catalog rather than trusted to match it.
/// <para>
/// Seeded with every baseline code this codebase itself emits. The baseline rows that are
/// missing are emitted by middleware this project does not own — 401/403 by the authentication
/// handler, 429 by the rate limiter — and 400 validation problems are identified by their
/// <c>errors</c> member rather than a code, by contract. Append one constant per product code
/// as capabilities emit them (references/host-components.md).
/// </para>
/// </summary>
public static class ProblemTypes
{
    private const string ProblemsBase = "urn:routego:seat-inventory:problems";

    /// <summary>404 — the resource does not exist.</summary>
    public static readonly ProblemType NotFound = new("NOT_FOUND", $"{ProblemsBase}:not-found");

    /// <summary>409 — state changed since read, or the request duplicates existing state.</summary>
    public static readonly ProblemType Conflict = new("CONFLICT", $"{ProblemsBase}:conflict");

    /// <summary>422 — a business rule refused the request and no specific code exists yet.</summary>
    public static readonly ProblemType BusinessRuleViolated =
        new("BUSINESS_RULE_VIOLATED", $"{ProblemsBase}:business-rule-violated");

    /// <summary>502 — a required dependency is down or returned an unusable response.</summary>
    public static readonly ProblemType UpstreamUnavailable =
        new("UPSTREAM_UNAVAILABLE", $"{ProblemsBase}:upstream-unavailable");

    /// <summary>504 — a required dependency did not respond in time and no data could be served.</summary>
    public static readonly ProblemType UpstreamTimeout =
        new("UPSTREAM_TIMEOUT", $"{ProblemsBase}:upstream-timeout");

    /// <summary>500 — unhandled server fault. Emitted by UnhandledExceptionHandler, never by a slice.</summary>
    public static readonly ProblemType InternalError =
        new("INTERNAL_ERROR", $"{ProblemsBase}:internal-error");

    /// <summary>
    /// 400 — the request's shape is invalid. This product's contract requires the code on 400
    /// answers too, alongside the <c>errors</c> member, so clients can switch on it like any other.
    /// </summary>
    public static readonly ProblemType ValidationFailed =
        new("VALIDATION_FAILED", $"{ProblemsBase}:validation-failed");

    /// <summary>401 — missing, expired or invalid access token. Written by AuthenticationProblems.</summary>
    public static readonly ProblemType Unauthorized = new("UNAUTHORIZED", $"{ProblemsBase}:unauthorized");

    /// <summary>403 — the caller's role may not do this, or the trip belongs to another operator.</summary>
    public static readonly ProblemType Forbidden = new("FORBIDDEN", $"{ProblemsBase}:forbidden");

    /// <summary>409 — some named seats are not free; nothing changed. Extension: unavailableSeats.</summary>
    public static readonly ProblemType SeatsUnavailable =
        new("SEATS_UNAVAILABLE", $"{ProblemsBase}:seats-unavailable");

    /// <summary>422 — some named seat labels do not exist on the trip; nothing changed. Extension: unknownSeats.</summary>
    public static readonly ProblemType UnknownSeats = new("UNKNOWN_SEATS", $"{ProblemsBase}:unknown-seats");

    /// <summary>409 — Fleet cancelled the trip.</summary>
    public static readonly ProblemType TripCancelled = new("TRIP_CANCELLED", $"{ProblemsBase}:trip-cancelled");

    /// <summary>409 — the trip's departure time has passed. Extension: departedAt.</summary>
    public static readonly ProblemType TripDeparted = new("TRIP_DEPARTED", $"{ProblemsBase}:trip-departed");

    /// <summary>422 — the hold length asked for is above the hold ceiling; nothing held. Extension: maxHoldSeconds.</summary>
    public static readonly ProblemType HoldTooLong = new("HOLD_TOO_LONG", $"{ProblemsBase}:hold-too-long");

    /// <summary>409 — the booking already holds or owns other seats on the trip. Extensions: seatNos, holdStatus.</summary>
    public static readonly ProblemType BookingAlreadyHasSeats =
        new("BOOKING_ALREADY_HAS_SEATS", $"{ProblemsBase}:booking-already-has-seats");

    /// <summary>409 — the booking holds no seats on the trip: released, expired, or never held.</summary>
    public static readonly ProblemType HoldNotActive = new("HOLD_NOT_ACTIVE", $"{ProblemsBase}:hold-not-active");

    /// <summary>409 — the hold's seats are Booked; only Booking's cancellation frees them. Extension: seatNos.</summary>
    public static readonly ProblemType HoldAlreadyConfirmed =
        new("HOLD_ALREADY_CONFIRMED", $"{ProblemsBase}:hold-already-confirmed");

    /// <summary>409 — a seat change moves from seats the booking has not booked. Extension: seatNos.</summary>
    public static readonly ProblemType SeatsNotOwned = new("SEATS_NOT_OWNED", $"{ProblemsBase}:seats-not-owned");

    /// <summary>422 — a seat change moves to a seat of a different class. Extension: changes.</summary>
    public static readonly ProblemType SeatClassMismatch =
        new("SEAT_CLASS_MISMATCH", $"{ProblemsBase}:seat-class-mismatch");

    /// <summary>409 — an unblock names seats neither blocked by the operator nor Available. Extension: seatNos.</summary>
    public static readonly ProblemType SeatsNotOperatorBlocked =
        new("SEATS_NOT_OPERATOR_BLOCKED", $"{ProblemsBase}:seats-not-operator-blocked");

    /// <summary>
    /// No HTTP status — a platform engineer alert, written as a log line: a Fleet event (TripScheduled,
    /// FareChanged, TripCancelled) could not be applied. Carries eventId, eventType and reason
    /// (invalidEventData, tripUnknown, repeatedFailure).
    /// </summary>
    public static readonly ProblemType FleetEventNotApplied =
        new("FLEET_EVENT_NOT_APPLIED", $"{ProblemsBase}:fleet-event-not-applied");

    /// <summary>
    /// No HTTP status — a platform engineer alert, written as a log line: a Booking event (BookingFailed,
    /// BookingCancelled) could not be applied. Carries eventId, eventType and reason (invalidEventData,
    /// repeatedFailure).
    /// </summary>
    public static readonly ProblemType BookingEventNotApplied =
        new("BOOKING_EVENT_NOT_APPLIED", $"{ProblemsBase}:booking-event-not-applied");
}
