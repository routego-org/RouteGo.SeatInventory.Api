namespace Api.Features.TripSeats.RepublishSeats;

/// <summary>
/// CAP-SEAT-009: the taken seats of chosen trips are re-sent as their existing seat events, marked
/// republished and stamped with the time their seats last changed. The job reads a real seat store and
/// publishes to a real topic; the tests read what a subscriber receives.
/// </summary>
public sealed class RepublishSeatsTests : IClassFixture<RepublishSeatsSeatStore>
{
    private const string HeldAt = "2026-10-24T09:55:00.0000000Z";
    private const string HoldExpiresAt = "2026-10-24T10:10:00.0000000Z";
    private const string BookedFirstAt = "2026-10-24T08:00:00.0000000Z";
    private const string BookedLastAt = "2026-10-24T08:30:00.0000000Z";
    private const string BlockedAt = "2026-10-24T07:00:00.0000000Z";

    private readonly RepublishSeatsSeatStore _store;

    public RepublishSeatsTests(RepublishSeatsSeatStore store)
    {
        _store = store;
    }

    // AC-1
    [Fact]
    public async Task HandleAsync_NamedTripWithTakenSeats_SendsEachTakenSeatAsItsEventMarkedRepublished()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        TakenSeats taken = await SeedTripWithTakenSeatsAsync(tripId);

        // Act
        RepublishSeatsResult result = await _store.Handler!.HandleAsync(Named(tripId), CancellationToken.None);
        List<RepublishedSeatEvent> events = await _store.ReceiveSeatEventsAsync(tripId);

        // Assert
        JsonElement held = DataOf(events, "SeatHeld");
        JsonElement booked = DataOf(events, "SeatBooked");
        JsonElement blocked = DataOf(events, "SeatBlocked");
        Assert.Equal(3, events.Count);
        Assert.Equal(taken.HeldBookingId.ToString("D"), held.GetProperty("bookingId").GetString());
        Assert.Equal(new[] { "3A", "3B" }, SeatNosOf(held));
        Assert.Equal(HoldExpiresAt, held.GetProperty("holdExpiresAt").GetString());
        Assert.Equal(taken.BookedBookingId.ToString("D"), booked.GetProperty("bookingId").GetString());
        Assert.Equal(new[] { "4C", "4D" }, SeatNosOf(booked));
        Assert.Equal(new[] { "L1" }, SeatNosOf(blocked));
        Assert.Equal(RepublishSeatsSeatStore.OperatorId(tripId), blocked.GetProperty("operatorId").GetString());
        Assert.All(events, seatEvent => Assert.True(seatEvent.Envelope.GetProperty("data").GetProperty("republished").GetBoolean()));
        Assert.All(events, seatEvent => Assert.Equal(seatEvent.Envelope.GetProperty("eventType").GetString(), seatEvent.EventTypeAttribute));
        Assert.DoesNotContain(events, seatEvent => SeatNosOf(seatEvent.Envelope.GetProperty("data")).Contains("5A"));
        RepublishTripReport report = Assert.Single(result.Trips);
        Assert.Equal(RepublishTripOutcome.Republished, report.Outcome);
        Assert.Equal(3, report.EventCount);
    }

    // AC-1: each event's time is when its own seats last changed
    [Fact]
    public async Task HandleAsync_SeatsChangedAtDifferentTimes_StampsEachEventWithItsLatestSeatChange()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        await SeedTripWithTakenSeatsAsync(tripId);

        // Act
        await _store.Handler!.HandleAsync(Named(tripId), CancellationToken.None);
        List<RepublishedSeatEvent> events = await _store.ReceiveSeatEventsAsync(tripId);

        // Assert
        Assert.Equal(HeldAt, OccurredAtOf(events, "SeatHeld"));
        Assert.Equal(BookedLastAt, OccurredAtOf(events, "SeatBooked"));
        Assert.Equal(BlockedAt, OccurredAtOf(events, "SeatBlocked"));
    }

    // AC-2
    [Fact]
    public async Task HandleAsync_AllUpcoming_RepublishesUpcomingTripsAndSkipsDepartedAndCancelledOnes()
    {
        // Arrange
        Guid first = await SeedTripWithOneBookedSeatAsync(RepublishSeatsSeatStore.UpcomingDeparture, false);
        Guid second = await SeedTripWithOneBookedSeatAsync(RepublishSeatsSeatStore.UpcomingDeparture, false);
        Guid departed = await SeedTripWithOneBookedSeatAsync(RepublishSeatsSeatStore.PastDeparture, false);
        Guid cancelled = await SeedTripWithOneBookedSeatAsync(RepublishSeatsSeatStore.UpcomingDeparture, true);

        // Act
        var request = new RepublishSeatsRequest { IsAllUpcoming = true };
        RepublishSeatsResult result = await _store.Handler!.HandleAsync(request, CancellationToken.None);

        // Assert
        var reported = result.Trips.Select(trip => trip.TripId).ToList();
        Assert.Contains(first, reported);
        Assert.Contains(second, reported);
        Assert.DoesNotContain(departed, reported);
        Assert.DoesNotContain(cancelled, reported);
        Assert.Single(await _store.ReceiveSeatEventsAsync(first));
        Assert.Empty(await _store.ReceiveSeatEventsAsync(departed));
        Assert.Empty(await _store.ReceiveSeatEventsAsync(cancelled));
    }

    // AC-3
    [Fact]
    public async Task HandleAsync_NamedTripsThatCannotBeRepublished_AreSkippedAndReportedWhileTheOthersAreSent()
    {
        // Arrange
        Guid upcoming = await SeedTripWithOneBookedSeatAsync(RepublishSeatsSeatStore.UpcomingDeparture, false);
        Guid departed = await SeedTripWithOneBookedSeatAsync(RepublishSeatsSeatStore.PastDeparture, false);
        Guid cancelled = await SeedTripWithOneBookedSeatAsync(RepublishSeatsSeatStore.UpcomingDeparture, true);
        var unknown = Guid.NewGuid();
        var request = new RepublishSeatsRequest { TripIds = new List<Guid> { upcoming, departed, cancelled, unknown } };

        // Act
        RepublishSeatsResult result = await _store.Handler!.HandleAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(RepublishTripOutcome.Republished, OutcomeOf(result, upcoming));
        Assert.Equal(RepublishTripOutcome.Departed, OutcomeOf(result, departed));
        Assert.Equal(RepublishTripOutcome.Cancelled, OutcomeOf(result, cancelled));
        Assert.Equal(RepublishTripOutcome.NotKnown, OutcomeOf(result, unknown));
        Assert.Single(await _store.ReceiveSeatEventsAsync(upcoming));
        Assert.Empty(await _store.ReceiveSeatEventsAsync(departed));
        Assert.Empty(await _store.ReceiveSeatEventsAsync(cancelled));
        Assert.False(result.HasFailures);
    }

    // AC-4
    [Fact]
    public async Task HandleAsync_TripWithNoTakenSeats_SendsNothingAndReportsZeroEvents()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        await _store.PutTripAsync(tripId, RepublishSeatsSeatStore.UpcomingDeparture);
        await _store.PutSeatAsync(tripId, Seat("1A", "Available", BookedFirstAt));

        // Act
        RepublishSeatsResult result = await _store.Handler!.HandleAsync(Named(tripId), CancellationToken.None);

        // Assert
        RepublishTripReport report = Assert.Single(result.Trips);
        Assert.Equal(RepublishTripOutcome.Republished, report.Outcome);
        Assert.Equal(0, report.EventCount);
        Assert.Empty(await _store.ReceiveSeatEventsAsync(tripId));
    }

    // AC-5: the event is never newer than the seats it describes
    [Fact]
    public async Task HandleAsync_SeatLastChangedEarlierThanTheRun_OccurredAtIsTheChangeTimeNotTheRunTime()
    {
        // Arrange
        Guid tripId = await SeedTripWithOneBookedSeatAsync(RepublishSeatsSeatStore.UpcomingDeparture, false);

        // Act
        await _store.Handler!.HandleAsync(Named(tripId), CancellationToken.None);
        List<RepublishedSeatEvent> events = await _store.ReceiveSeatEventsAsync(tripId);

        // Assert
        string occurredAt = OccurredAtOf(events, "SeatBooked");
        Assert.Equal(BookedFirstAt, occurredAt);
        Assert.NotEqual(RepublishSeatsSeatStore.Now.UtcDateTime.ToString("O"), occurredAt);
    }

    // AC-6
    [Fact]
    public async Task HandleAsync_Run_ChangesNoSeatAndSendsOnlyHeldBookedAndBlockedEvents()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        await SeedTripWithTakenSeatsAsync(tripId);
        string before = Describe(await _store.ReadTripAsync(tripId));

        // Act
        await _store.Handler!.HandleAsync(Named(tripId), CancellationToken.None);
        List<RepublishedSeatEvent> events = await _store.ReceiveSeatEventsAsync(tripId);

        // Assert
        string after = Describe(await _store.ReadTripAsync(tripId));
        Assert.Equal(before, after);
        Assert.All(events, seatEvent => Assert.Contains(seatEvent.EventTypeAttribute, new[] { "SeatHeld", "SeatBooked", "SeatBlocked" }));
    }

    // AC-7
    [Fact]
    public async Task HandleAsync_RunTwice_SendsTheSameSeatsAndTimesWithNewEventIds()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        await SeedTripWithTakenSeatsAsync(tripId);
        await _store.Handler!.HandleAsync(Named(tripId), CancellationToken.None);
        List<RepublishedSeatEvent> firstRun = await _store.ReceiveSeatEventsAsync(tripId);

        // Act
        await _store.Handler!.HandleAsync(Named(tripId), CancellationToken.None);
        List<RepublishedSeatEvent> secondRun = await _store.ReceiveSeatEventsAsync(tripId);

        // Assert
        Assert.Equal(firstRun.Count, secondRun.Count);
        Assert.Equal(SeatsAndTimes(firstRun), SeatsAndTimes(secondRun));
        Assert.Empty(EventIds(firstRun).Intersect(EventIds(secondRun)));
    }

    // Repeat safety within one run: a send retried after a failure is the same event to a subscriber
    [Fact]
    public async Task HandleAsync_SameCorrelationIdTwice_SendsEventsWithTheSameEventIds()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        await SeedTripWithTakenSeatsAsync(tripId);
        var request = new RepublishSeatsRequest { TripIds = new List<Guid> { tripId }, CorrelationId = "run-1" };
        await _store.Handler!.HandleAsync(request, CancellationToken.None);
        List<RepublishedSeatEvent> firstRun = await _store.ReceiveSeatEventsAsync(tripId);

        // Act
        await _store.Handler!.HandleAsync(request, CancellationToken.None);
        List<RepublishedSeatEvent> secondRun = await _store.ReceiveSeatEventsAsync(tripId);

        // Assert
        Assert.Equal(EventIds(firstRun).Order().ToList(), EventIds(secondRun).Order().ToList());
        Assert.All(secondRun, seatEvent => Assert.Equal("run-1", seatEvent.Envelope.GetProperty("correlationId").GetString()));
    }

    // AC-8
    [Fact]
    public async Task HandleAsync_MessagingServiceDown_ReportsTheTripFailedAndTheRunFailed()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        await SeedTripWithTakenSeatsAsync(tripId);
        RepublishSeatsHandler handler = _store.HandlerDuringOutage();

        // Act
        RepublishSeatsResult result = await handler.HandleAsync(Named(tripId), CancellationToken.None);

        // Assert
        RepublishTripReport report = Assert.Single(result.Trips);
        Assert.Equal(RepublishTripOutcome.Failed, report.Outcome);
        Assert.NotNull(report.Failure);
        Assert.True(result.HasFailures);
    }

    // AC-8: the run goes on with the other trips
    [Fact]
    public async Task HandleAsync_OneTripCannotBeRead_StillRepublishesTheOthersAndEndsAsFailed()
    {
        // Arrange
        var broken = Guid.NewGuid();
        await _store.PutTripAsync(broken, RepublishSeatsSeatStore.UpcomingDeparture);
        await _store.PutSeatAsync(broken, Seat("2A", "Held", HeldAt));
        Guid healthy = await SeedTripWithOneBookedSeatAsync(RepublishSeatsSeatStore.UpcomingDeparture, false);
        var request = new RepublishSeatsRequest { TripIds = new List<Guid> { broken, healthy } };

        // Act
        RepublishSeatsResult result = await _store.Handler!.HandleAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(RepublishTripOutcome.Failed, OutcomeOf(result, broken));
        Assert.Equal(RepublishTripOutcome.Republished, OutcomeOf(result, healthy));
        Assert.Single(await _store.ReceiveSeatEventsAsync(healthy));
        Assert.True(result.HasFailures);
    }

    // The correlation identifier is the run's: the one given, or a generated one
    [Fact]
    public async Task HandleAsync_NoCorrelationIdGiven_GeneratesOneForTheWholeRun()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        await SeedTripWithTakenSeatsAsync(tripId);

        // Act
        RepublishSeatsResult result = await _store.Handler!.HandleAsync(Named(tripId), CancellationToken.None);
        List<RepublishedSeatEvent> events = await _store.ReceiveSeatEventsAsync(tripId);

        // Assert
        Assert.False(string.IsNullOrWhiteSpace(result.CorrelationId));
        Assert.All(events, seatEvent => Assert.Equal(result.CorrelationId, seatEvent.Envelope.GetProperty("correlationId").GetString()));
    }

    private static RepublishSeatsRequest Named(Guid tripId)
    {
        return new RepublishSeatsRequest { TripIds = new List<Guid> { tripId } };
    }

    private static RepublishSeatsSeed Seat(string seatNo, string status, string changedAt)
    {
        return new RepublishSeatsSeed { SeatNo = seatNo, Status = status, ChangedAt = changedAt };
    }

    // Held 3A and 3B by one booking, booked 4C and 4D by another at different times, L1 blocked by the
    // operator, and 5A left Available.
    private async Task<TakenSeats> SeedTripWithTakenSeatsAsync(Guid tripId)
    {
        var heldBookingId = Guid.NewGuid();
        var bookedBookingId = Guid.NewGuid();
        await _store.PutTripAsync(tripId, RepublishSeatsSeatStore.UpcomingDeparture);
        foreach (string seatNo in new[] { "3A", "3B" })
        {
            var held = new RepublishSeatsSeed
            {
                SeatNo = seatNo,
                Status = "Held",
                ChangedAt = HeldAt,
                BookingId = heldBookingId,
                HoldExpiresAt = HoldExpiresAt,
            };
            await _store.PutSeatAsync(tripId, held);
        }

        await _store.PutSeatAsync(tripId, new RepublishSeatsSeed { SeatNo = "4C", Status = "Booked", ChangedAt = BookedFirstAt, BookingId = bookedBookingId });
        await _store.PutSeatAsync(tripId, new RepublishSeatsSeed { SeatNo = "4D", Status = "Booked", ChangedAt = BookedLastAt, BookingId = bookedBookingId });
        await _store.PutSeatAsync(tripId, new RepublishSeatsSeed { SeatNo = "L1", Status = "Blocked", ChangedAt = BlockedAt, BlockReason = "Operator" });
        await _store.PutSeatAsync(tripId, Seat("5A", "Available", BookedFirstAt));
        return new TakenSeats(heldBookingId, bookedBookingId);
    }

    private async Task<Guid> SeedTripWithOneBookedSeatAsync(string departureAt, bool isCancelled)
    {
        var tripId = Guid.NewGuid();
        await _store.PutTripAsync(tripId, departureAt, isCancelled);
        await _store.PutSeatAsync(tripId, new RepublishSeatsSeed { SeatNo = "1A", Status = "Booked", ChangedAt = BookedFirstAt, BookingId = Guid.NewGuid() });
        return tripId;
    }

    private static RepublishTripOutcome OutcomeOf(RepublishSeatsResult result, Guid tripId)
    {
        return result.Trips.Single(trip => trip.TripId == tripId).Outcome;
    }

    private static JsonElement DataOf(List<RepublishedSeatEvent> events, string eventType)
    {
        return events.Single(seatEvent => seatEvent.EventTypeAttribute == eventType).Envelope.GetProperty("data");
    }

    private static string OccurredAtOf(List<RepublishedSeatEvent> events, string eventType)
    {
        return events.Single(seatEvent => seatEvent.EventTypeAttribute == eventType).Envelope.GetProperty("occurredAt").GetString()!;
    }

    private static List<string> SeatNosOf(JsonElement data)
    {
        return data.GetProperty("seatNos").EnumerateArray().Select(seatNo => seatNo.GetString()!).ToList();
    }

    private static List<string> EventIds(List<RepublishedSeatEvent> events)
    {
        return events.Select(seatEvent => seatEvent.Envelope.GetProperty("eventId").GetString()!).ToList();
    }

    // Everything about an event except its identity and correlation: type, seats, booking and time.
    private static List<string> SeatsAndTimes(List<RepublishedSeatEvent> events)
    {
        var lines = events.Select(Summarize).ToList();
        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    private static string Summarize(RepublishedSeatEvent seatEvent)
    {
        JsonElement data = seatEvent.Envelope.GetProperty("data");
        string occurredAt = seatEvent.Envelope.GetProperty("occurredAt").GetString()!;
        return seatEvent.EventTypeAttribute + "|" + occurredAt + "|" + string.Join(",", SeatNosOf(data)) + "|" + data.GetRawText();
    }

    private static string Describe(List<Dictionary<string, AttributeValue>> items)
    {
        IEnumerable<string> lines = items.Select(item => string.Join(";", item.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value.S)));
        return string.Join("\n", lines);
    }

    /// <summary>The two bookings a seeded trip's seats belong to.</summary>
    private sealed record TakenSeats(Guid HeldBookingId, Guid BookedBookingId);
}
