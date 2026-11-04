namespace Api.Domain;

/// <summary>Whether a seat can be sold right now.</summary>
public enum SeatStatus
{
    Available,
    Held,
    Booked,
    Blocked,
}
