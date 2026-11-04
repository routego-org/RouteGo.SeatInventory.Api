namespace Api.Domain;

/// <summary>Where a booking's hold on a trip stands: Held until Booking confirms it, then Booked.</summary>
public enum HoldStatus
{
    Held,
    Booked,
}
