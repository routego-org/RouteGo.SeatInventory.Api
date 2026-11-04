namespace Api.Features.TripSeats.RepublishSeats;

/// <summary>
/// Turns the taken seats of one trip into the seat events that describe them now: one SeatHeld per
/// booking holding seats, one SeatBooked per booking owning seats, and one SeatBlocked for every seat
/// the operator blocked. Each event is marked republished and is stamped with the time its seats last
/// changed, never the time of the run, so a subscriber that keeps the latest change per seat is never
/// moved backwards by it. No I/O, no clock.
/// </summary>
internal static class RepublishedSeatEvents
{
    private const string SeatHeld = "SeatHeld";
    private const string SeatBooked = "SeatBooked";
    private const string SeatBlocked = "SeatBlocked";

    /// <summary>The events for the trip's seats, held first, then booked, then blocked.</summary>
    /// <param name="tripId">The trip.</param>
    /// <param name="operatorId">The trip's operator, named by the blocked-seats event.</param>
    /// <param name="seats">The trip's seat records as stored; Available seats are ignored.</param>
    /// <param name="correlationId">The run's correlation identifier.</param>
    public static List<SeatEventMessage> Plan(Guid tripId, string operatorId, List<Dictionary<string, AttributeValue>> seats, string correlationId)
    {
        var messages = new List<SeatEventMessage>();
        foreach (IGrouping<string, Dictionary<string, AttributeValue>> hold in GroupByBooking(seats, nameof(SeatStatus.Held)))
        {
            messages.Add(PlanHeld(tripId, hold.Key, hold.ToList(), correlationId));
        }

        foreach (IGrouping<string, Dictionary<string, AttributeValue>> booking in GroupByBooking(seats, nameof(SeatStatus.Booked)))
        {
            messages.Add(PlanBooked(tripId, booking.Key, booking.ToList(), correlationId));
        }

        List<Dictionary<string, AttributeValue>> blocked = seats.FindAll(IsOperatorBlocked);
        if (blocked.Count > 0)
        {
            messages.Add(PlanBlocked(tripId, operatorId, blocked, correlationId));
        }

        return messages;
    }

    private static SeatEventMessage PlanHeld(Guid tripId, string bookingId, List<Dictionary<string, AttributeValue>> seats, string correlationId)
    {
        var data = new SeatHeldData
        {
            TripId = tripId.ToString("D"),
            BookingId = bookingId,
            SeatNos = SeatNosOf(seats),
            HoldExpiresAt = seats[0]["holdExpiresAt"].S,
            Republished = true,
        };
        Guid eventId = EventIdOf(correlationId, tripId, bookingId, SeatHeld);
        return SeatEventEnvelopes.ToMessage(SeatEventStream.Inventory, SeatHeld, eventId, StampOf(seats, correlationId), data);
    }

    private static SeatEventMessage PlanBooked(Guid tripId, string bookingId, List<Dictionary<string, AttributeValue>> seats, string correlationId)
    {
        var data = new SeatBookedData
        {
            TripId = tripId.ToString("D"),
            BookingId = bookingId,
            SeatNos = SeatNosOf(seats),
            Republished = true,
        };
        Guid eventId = EventIdOf(correlationId, tripId, bookingId, SeatBooked);
        return SeatEventEnvelopes.ToMessage(SeatEventStream.Inventory, SeatBooked, eventId, StampOf(seats, correlationId), data);
    }

    private static SeatEventMessage PlanBlocked(Guid tripId, string operatorId, List<Dictionary<string, AttributeValue>> seats, string correlationId)
    {
        var data = new SeatBlockData
        {
            TripId = tripId.ToString("D"),
            SeatNos = SeatNosOf(seats),
            OperatorId = operatorId,
            Republished = true,
        };
        Guid eventId = EventIdOf(correlationId, tripId, string.Empty, SeatBlocked);
        return SeatEventEnvelopes.ToMessage(SeatEventStream.Inventory, SeatBlocked, eventId, StampOf(seats, correlationId), data);
    }

    // The identity names the run, the trip, the event type and the booking, so an event sent again after
    // a retry is the same event to a subscriber, while the next run's events are new ones.
    private static Guid EventIdOf(string correlationId, Guid tripId, string bookingId, string eventType)
    {
        string name = correlationId + ":" + tripId.ToString("D") + ":" + bookingId;
        return SeatEventEnvelopes.DeriveEventId(name, eventType);
    }

    private static IEnumerable<IGrouping<string, Dictionary<string, AttributeValue>>> GroupByBooking(
        List<Dictionary<string, AttributeValue>> seats, string status)
    {
        List<Dictionary<string, AttributeValue>> taken = seats.FindAll(seat => seat["status"].S == status);
        return taken.GroupBy(seat => seat["bookingId"].S).OrderBy(group => group.Key, StringComparer.Ordinal);
    }

    private static bool IsOperatorBlocked(Dictionary<string, AttributeValue> seat)
    {
        bool isBlocked = seat["status"].S == nameof(SeatStatus.Blocked);
        return isBlocked && seat.ContainsKey("blockReason") && seat["blockReason"].S == nameof(BlockReason.Operator);
    }

    private static List<string> SeatNosOf(List<Dictionary<string, AttributeValue>> seats)
    {
        List<string> seatNos = seats.ConvertAll(seat => seat["seatNo"].S);
        seatNos.Sort(StringComparer.Ordinal);
        return seatNos;
    }

    // The latest change among the event's seats: the event must not look newer than any seat it names.
    private static EventStamp StampOf(List<Dictionary<string, AttributeValue>> seats, string correlationId)
    {
        DateTimeOffset latest = seats.Max(seat => ParseUtc(seat["lastChangedAt"].S));
        return new EventStamp(latest.UtcDateTime.ToString("O"), correlationId);
    }

    private static DateTimeOffset ParseUtc(string text)
    {
        return DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal);
    }
}
