namespace Api.Domain;

/// <summary>One move of a seat change: a booking leaves one seat for another of the same class.</summary>
/// <param name="FromSeatNo">The seat the booking leaves; it becomes Available.</param>
/// <param name="ToSeatNo">The seat the booking takes; it becomes Booked by the booking.</param>
public sealed record SeatMove(string FromSeatNo, string ToSeatNo);
