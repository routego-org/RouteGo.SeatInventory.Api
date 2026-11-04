namespace Api.Features.Holds.ReleaseCancelledBooking;

/// <summary>
/// The BookingCancelled data Seat Inventory needs: the booking and its trip. Taken from RouteGo's system
/// event catalog, because Booking has no specs or code yet. Seat labels, refund amount and reason in the
/// event are not read — the seats freed are the ones recorded for the booking, so a stale or partial list
/// can never free someone else's seat. Nullable so a missing identifier is reported, not thrown.
/// </summary>
public sealed record BookingCancelledMessage
{
    public Guid? BookingId { get; init; }

    public Guid? TripId { get; init; }
}

/// <summary>What one BookingCancelled freed.</summary>
/// <param name="ReleasedSeatNos">The seats freed; empty when the booking owned none any more.</param>
public sealed record ReleaseCancelledBookingResult(IReadOnlyList<string> ReleasedSeatNos);
