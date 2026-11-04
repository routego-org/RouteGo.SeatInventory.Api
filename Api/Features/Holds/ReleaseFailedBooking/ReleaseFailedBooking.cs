namespace Api.Features.Holds.ReleaseFailedBooking;

/// <summary>
/// The BookingFailed data Seat Inventory needs: the booking and its trip. Taken from RouteGo's system
/// event catalog, because Booking has no specs or code yet. Seat labels in the event are not read — the
/// seats freed are the ones recorded for the booking. Nullable so a missing identifier is reported, not thrown.
/// </summary>
public sealed record BookingFailedMessage
{
    public Guid? BookingId { get; init; }

    public Guid? TripId { get; init; }
}

/// <summary>What one BookingFailed freed.</summary>
/// <param name="ReleasedSeatNos">The seats freed; empty when the booking held none any more.</param>
public sealed record ReleaseFailedBookingResult(IReadOnlyList<string> ReleasedSeatNos);
