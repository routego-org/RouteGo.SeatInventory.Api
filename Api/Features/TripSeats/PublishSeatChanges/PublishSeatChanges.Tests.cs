namespace Api.Features.TripSeats.PublishSeatChanges;

/// <summary>
/// CAP-SEAT-008: every recorded seat change becomes its seat event (and audit record), delivered at
/// least once. Seats are changed the way the write slices change them; the publisher reads the real
/// change stream and publishes to real topics, and the tests read what a subscriber receives.
/// </summary>
public sealed class PublishSeatChangesTests : IClassFixture<PublishSeatChangesSeatStore>
{
    private readonly PublishSeatChangesSeatStore _store;

    public PublishSeatChangesTests(PublishSeatChangesSeatStore store)
    {
        _store = store;
        _store.Clock.UtcNow = PublishSeatChangesTestClock.RealNow();
    }

    // AC-1
    [Fact]
    public async Task HandleAsync_TwoSeatsHeldInOneRequest_PublishesOneSeatHeldNamingBoth()
    {
        // Arrange
        Guid tripId = await CreateTripAsync("3A", "3B");
        var bookingId = Guid.NewGuid();
        string request = NewRequest();
        Dictionary<string, string?> held = Record("Held", "Held", request);
        held["bookingId"] = bookingId.ToString("D");
        held["holdExpiresAt"] = "2026-10-24T10:10:00.0000000Z";
        await _store.ChangeSeatsAsync(tripId, new[] { "3A", "3B" }, held);

        // Act
        await _store.Handler!.HandleAsync(CancellationToken.None);
        List<PublishedSeatEvent> events = await _store.ReceiveSeatEventsAsync(tripId);

        // Assert
        PublishedSeatEvent seatHeld = Assert.Single(events);
        JsonElement envelope = seatHeld.Envelope;
        JsonElement data = envelope.GetProperty("data");
        Assert.Equal("SeatHeld", seatHeld.EventTypeAttribute);
        Assert.Equal("SeatHeld", envelope.GetProperty("eventType").GetString());
        Assert.Equal(request, envelope.GetProperty("eventId").GetString());
        Assert.Equal(1, envelope.GetProperty("version").GetInt32());
        Assert.Equal("seat-inventory-service", envelope.GetProperty("producer").GetString());
        Assert.Equal("correlation-" + request, envelope.GetProperty("correlationId").GetString());
        Assert.Equal(PublishSeatChangesSeatStore.ChangedAt, envelope.GetProperty("occurredAt").GetString());
        Assert.Equal(bookingId.ToString("D"), data.GetProperty("bookingId").GetString());
        Assert.Equal(new[] { "3A", "3B" }, SeatNosOf(data));
        Assert.Equal("2026-10-24T10:10:00.0000000Z", data.GetProperty("holdExpiresAt").GetString());
    }

    // A real change is not a re-sent one: the republished field is absent
    [Fact]
    public async Task HandleAsync_RealSeatChange_PublishesEventWithoutTheRepublishedField()
    {
        // Arrange
        Guid tripId = await CreateTripAsync("2A");
        Dictionary<string, string?> held = Record("Held", "Held", NewRequest());
        held["bookingId"] = Guid.NewGuid().ToString("D");
        held["holdExpiresAt"] = "2026-10-24T10:10:00.0000000Z";
        await _store.ChangeSeatsAsync(tripId, new[] { "2A" }, held);

        // Act
        await _store.Handler!.HandleAsync(CancellationToken.None);
        List<PublishedSeatEvent> events = await _store.ReceiveSeatEventsAsync(tripId);

        // Assert
        PublishedSeatEvent seatHeld = Assert.Single(events);
        Assert.False(seatHeld.Envelope.GetProperty("data").TryGetProperty("republished", out _));
    }

    // AC-2: the confirm gives SeatBooked
    [Fact]
    public async Task HandleAsync_HoldConfirmed_PublishesSeatBooked()
    {
        // Arrange
        Guid tripId = await CreateTripAsync("4A");
        var bookingId = Guid.NewGuid();
        Dictionary<string, string?> booked = Record("Booked", "Booked", NewRequest());
        booked["bookingId"] = bookingId.ToString("D");
        await _store.ChangeSeatsAsync(tripId, new[] { "4A" }, booked);

        // Act
        await _store.Handler!.HandleAsync(CancellationToken.None);
        List<PublishedSeatEvent> events = await _store.ReceiveSeatEventsAsync(tripId);

        // Assert
        PublishedSeatEvent seatBooked = Assert.Single(events);
        JsonElement data = seatBooked.Envelope.GetProperty("data");
        Assert.Equal("SeatBooked", seatBooked.EventTypeAttribute);
        Assert.Equal(bookingId.ToString("D"), data.GetProperty("bookingId").GetString());
        Assert.Equal(new[] { "4A" }, SeatNosOf(data));
    }

    // AC-2: a release gives SeatReleased with its reason and what the seats went to; the booking it
    // freed is the one the seats had before
    [Fact]
    public async Task HandleAsync_HoldExpiredOnCancelledTrip_PublishesSeatReleasedToBlockedNamingTheFreedBooking()
    {
        // Arrange
        var bookingId = Guid.NewGuid();
        Guid tripId = await CreateTripAsync();
        await _store.PutSeatAsync(tripId, "5A", "Held", bookingId);
        Dictionary<string, string?> released = Record("Blocked", "HoldExpired", NewRequest());
        released["blockReason"] = "TripCancelled";
        released["bookingId"] = null;
        released["holdExpiresAt"] = null;
        await _store.ChangeSeatsAsync(tripId, new[] { "5A" }, released);

        // Act
        await _store.Handler!.HandleAsync(CancellationToken.None);
        List<PublishedSeatEvent> events = await _store.ReceiveSeatEventsAsync(tripId);

        // Assert
        PublishedSeatEvent seatReleased = Assert.Single(events);
        JsonElement data = seatReleased.Envelope.GetProperty("data");
        Assert.Equal("SeatReleased", seatReleased.EventTypeAttribute);
        Assert.Equal(bookingId.ToString("D"), data.GetProperty("bookingId").GetString());
        Assert.Equal("HoldExpired", data.GetProperty("reason").GetString());
        Assert.Equal("Blocked", data.GetProperty("releasedTo").GetString());
        Assert.Equal(new[] { "5A" }, SeatNosOf(data));
    }

    // AC-2 and AC-4: an operator block gives SeatBlocked naming the trip's operator, and an audit record
    [Fact]
    public async Task HandleAsync_OperatorBlocksSeats_PublishesSeatBlockedAndAnAuditRecord()
    {
        // Arrange
        var operatorId = Guid.NewGuid();
        var operatorUserId = Guid.NewGuid();
        var tripId = Guid.NewGuid();
        await _store.PutTripAsync(tripId, operatorId);
        await _store.PutSeatAsync(tripId, "6A", "Available", null);
        Dictionary<string, string?> blocked = Record("Blocked", "OperatorBlocked", NewRequest());
        blocked["blockReason"] = "Operator";
        blocked["lastChangedBy"] = operatorUserId.ToString("D");
        await _store.ChangeSeatsAsync(tripId, new[] { "6A" }, blocked);

        // Act
        await _store.Handler!.HandleAsync(CancellationToken.None);
        List<PublishedSeatEvent> events = await _store.ReceiveSeatEventsAsync(tripId);
        List<PublishedSeatEvent> audits = await _store.ReceiveAuditRecordsAsync(tripId);

        // Assert
        PublishedSeatEvent seatBlocked = Assert.Single(events);
        Assert.Equal("SeatBlocked", seatBlocked.EventTypeAttribute);
        Assert.Equal(operatorId.ToString("D"), seatBlocked.Envelope.GetProperty("data").GetProperty("operatorId").GetString());
        PublishedSeatEvent audit = Assert.Single(audits);
        JsonElement auditData = audit.Envelope.GetProperty("data");
        Assert.Equal("AuditRecorded", audit.EventTypeAttribute);
        Assert.Equal(operatorUserId.ToString("D"), auditData.GetProperty("actorId").GetString());
        Assert.Equal("Operator", auditData.GetProperty("actorRole").GetString());
        Assert.Equal("SeatsBlocked", auditData.GetProperty("action").GetString());
        Assert.Equal("Trip", auditData.GetProperty("entityType").GetString());
        Assert.Equal(new[] { "6A" }, SeatNosOf(auditData.GetProperty("details")));
        Assert.NotEqual(seatBlocked.Envelope.GetProperty("eventId").GetString(), audit.Envelope.GetProperty("eventId").GetString());
    }

    // AC-4: an admin's cancellation gives SeatReleased and an audit record naming the booking and the note
    [Fact]
    public async Task HandleAsync_AdminCancelsHold_PublishesSeatReleasedAndAnAuditRecordWithTheNote()
    {
        // Arrange
        var bookingId = Guid.NewGuid();
        var adminUserId = Guid.NewGuid();
        Guid tripId = await CreateTripAsync();
        await _store.PutSeatAsync(tripId, "7A", "Held", bookingId);
        Dictionary<string, string?> cancelled = Record("Available", "AdminReleased", NewRequest());
        cancelled["bookingId"] = null;
        cancelled["lastChangedBy"] = adminUserId.ToString("D");
        cancelled["lastChangeNote"] = "Customer called to cancel";
        await _store.ChangeSeatsAsync(tripId, new[] { "7A" }, cancelled);

        // Act
        await _store.Handler!.HandleAsync(CancellationToken.None);
        List<PublishedSeatEvent> events = await _store.ReceiveSeatEventsAsync(tripId);
        List<PublishedSeatEvent> audits = await _store.ReceiveAuditRecordsAsync(tripId);

        // Assert
        PublishedSeatEvent seatReleased = Assert.Single(events);
        Assert.Equal("AdminReleased", seatReleased.Envelope.GetProperty("data").GetProperty("reason").GetString());
        PublishedSeatEvent audit = Assert.Single(audits);
        JsonElement auditData = audit.Envelope.GetProperty("data");
        JsonElement details = auditData.GetProperty("details");
        Assert.Equal("Admin", auditData.GetProperty("actorRole").GetString());
        Assert.Equal("HoldCancelled", auditData.GetProperty("action").GetString());
        Assert.Equal(bookingId.ToString("D"), details.GetProperty("bookingId").GetString());
        Assert.Equal("Customer called to cancel", details.GetProperty("note").GetString());
    }

    // AC-2: a seat change gives SeatChanged with every from-seat and to-seat
    [Fact]
    public async Task HandleAsync_SeatsChanged_PublishesSeatChangedWithEveryMove()
    {
        // Arrange
        var bookingId = Guid.NewGuid();
        Guid tripId = await CreateTripAsync();
        await _store.PutSeatAsync(tripId, "1A", "Booked", bookingId);
        await _store.PutSeatAsync(tripId, "2A", "Available", null);
        string request = NewRequest();
        Dictionary<string, string?> freed = Record("Available", "SeatChanged", request);
        freed["bookingId"] = null;
        Dictionary<string, string?> taken = Record("Booked", "SeatChanged", request);
        taken["bookingId"] = bookingId.ToString("D");
        taken["lastChangeMovedFrom"] = "1A";
        await _store.ChangeSeatsAsync(tripId, new[] { "1A" }, freed);
        await _store.ChangeSeatsAsync(tripId, new[] { "2A" }, taken);

        // Act
        await _store.Handler!.HandleAsync(CancellationToken.None);
        List<PublishedSeatEvent> events = await _store.ReceiveSeatEventsAsync(tripId);

        // Assert
        PublishedSeatEvent seatChanged = Assert.Single(events);
        JsonElement data = seatChanged.Envelope.GetProperty("data");
        JsonElement move = Assert.Single(data.GetProperty("changes").EnumerateArray());
        Assert.Equal("SeatChanged", seatChanged.EventTypeAttribute);
        Assert.Equal(bookingId.ToString("D"), data.GetProperty("bookingId").GetString());
        Assert.Equal("1A", move.GetProperty("fromSeatNo").GetString());
        Assert.Equal("2A", move.GetProperty("toSeatNo").GetString());
    }

    // AC-3
    [Fact]
    public async Task HandleAsync_SeatsCreatedFareChangedOrCancellationBlocked_PublishesNothing()
    {
        // Arrange
        Guid tripId = await CreateTripAsync("8A", "8B");
        await _store.ChangeSeatsAsync(tripId, new[] { "8A" }, Record("Available", "FareChanged", NewRequest()));
        await _store.ChangeSeatsAsync(tripId, new[] { "8B" }, Record("Blocked", "TripCancelled", NewRequest()));

        // Act
        PublishSeatChangesResult result = await _store.Handler!.HandleAsync(CancellationToken.None);
        List<PublishedSeatEvent> events = await _store.ReceiveSeatEventsAsync(tripId);

        // Assert
        Assert.Empty(events);
        Assert.Empty(result.Unreadable);
    }

    // AC-5 and AC-7: nothing is lost while messaging is down, and the order of a seat's events holds
    [Fact]
    public async Task HandleAsync_MessagingDownThenBack_KeepsChangesThenDeliversThemInOrder()
    {
        // Arrange
        var bookingId = Guid.NewGuid();
        Guid tripId = await CreateTripAsync("9A");
        Dictionary<string, string?> held = Record("Held", "Held", NewRequest());
        held["bookingId"] = bookingId.ToString("D");
        held["holdExpiresAt"] = "2026-10-24T10:10:00.0000000Z";
        Dictionary<string, string?> booked = Record("Booked", "Booked", NewRequest());
        booked["holdExpiresAt"] = null;
        await _store.ChangeSeatsAsync(tripId, new[] { "9A" }, held);
        await _store.ChangeSeatsAsync(tripId, new[] { "9A" }, booked);

        // Act
        PublishSeatChangesResult duringOutage = await _store.HandlerDuringOutage().HandleAsync(CancellationToken.None);
        List<PublishedSeatEvent> eventsDuringOutage = await _store.ReceiveSeatEventsAsync(tripId);
        await _store.Handler!.HandleAsync(CancellationToken.None);
        List<PublishedSeatEvent> eventsAfterOutage = await _store.ReceiveSeatEventsAsync(tripId);

        // Assert
        Assert.NotEmpty(duringOutage.HeldBack);
        Assert.Empty(eventsDuringOutage);
        Assert.Equal(new[] { "SeatHeld", "SeatBooked" }, eventsAfterOutage.ConvertAll(seatEvent => seatEvent.EventTypeAttribute));
    }

    // AC-6
    [Fact]
    public async Task HandleAsync_DeliveryNotConfirmed_RepublishesWithTheSameEventId()
    {
        // Arrange
        Guid tripId = await CreateTripAsync("10A");
        Dictionary<string, string?> booked = Record("Booked", "Booked", NewRequest());
        booked["bookingId"] = Guid.NewGuid().ToString("D");
        await _store.ChangeSeatsAsync(tripId, new[] { "10A" }, booked);
        await _store.Handler!.HandleAsync(CancellationToken.None);
        List<PublishedSeatEvent> firstTime = await _store.ReceiveSeatEventsAsync(tripId);
        PublishedSeatEvent first = Assert.Single(firstTime);
        await _store.ForgetProgressAsync();

        // Act
        await _store.Handler.HandleAsync(CancellationToken.None);
        List<PublishedSeatEvent> again = await _store.ReceiveSeatEventsAsync(tripId);

        // Assert
        PublishedSeatEvent repeat = Assert.Single(again);
        Assert.Equal(first.Envelope.GetProperty("eventId").GetString(), repeat.Envelope.GetProperty("eventId").GetString());
    }

    // AC-8
    [Fact]
    public async Task HandleAsync_ChangeWaitingOverFiveMinutes_RaisesTheDelayAlertOnce()
    {
        // Arrange
        Guid tripId = await CreateTripAsync("11A");
        string request = NewRequest();
        Dictionary<string, string?> booked = Record("Booked", "Booked", request);
        booked["bookingId"] = Guid.NewGuid().ToString("D");
        await _store.ChangeSeatsAsync(tripId, new[] { "11A" }, booked);
        PublishSeatChangesHandler duringOutage = _store.HandlerDuringOutage();
        await duringOutage.HandleAsync(CancellationToken.None);
        _store.Clock.UtcNow = PublishSeatChangesTestClock.RealNow().AddMinutes(5).AddSeconds(1);

        // Act
        PublishSeatChangesResult firstNotice = await duringOutage.HandleAsync(CancellationToken.None);
        PublishSeatChangesResult nextRun = await duringOutage.HandleAsync(CancellationToken.None);

        // Assert
        DelayedSeatChange delayed = Assert.Single(firstNotice.Delayed);
        Assert.Equal(request, delayed.Request);
        Assert.True(delayed.LagSeconds > 300);
        Assert.Empty(nextRun.Delayed);
    }

    [Fact]
    public async Task HandleAsync_AnotherCopyHoldsTheLease_SkipsAndPublishesNothing()
    {
        // Arrange
        await _store.Handler!.HandleAsync(CancellationToken.None);
        Guid tripId = await CreateTripAsync("12A");
        Dictionary<string, string?> booked = Record("Booked", "Booked", NewRequest());
        booked["bookingId"] = Guid.NewGuid().ToString("D");
        await _store.ChangeSeatsAsync(tripId, new[] { "12A" }, booked);

        // Act
        PublishSeatChangesResult result = await _store.HandlerOfAnotherCopy().HandleAsync(CancellationToken.None);
        List<PublishedSeatEvent> events = await _store.ReceiveSeatEventsAsync(tripId);

        // Assert
        Assert.True(result.IsSkipped);
        Assert.Empty(events);
    }

    [Fact]
    public async Task HandleAsync_UnreadableChange_ReportsItAndStillPublishesLaterChanges()
    {
        // Arrange
        Guid tripId = await CreateTripAsync("13A", "13B");
        string unreadableRequest = NewRequest();
        await _store.ChangeSeatsAsync(tripId, new[] { "13A" }, Record("Available", "Teleported", unreadableRequest));
        Dictionary<string, string?> booked = Record("Booked", "Booked", NewRequest());
        booked["bookingId"] = Guid.NewGuid().ToString("D");
        await _store.ChangeSeatsAsync(tripId, new[] { "13B" }, booked);

        // Act
        PublishSeatChangesResult result = await _store.Handler!.HandleAsync(CancellationToken.None);
        List<PublishedSeatEvent> events = await _store.ReceiveSeatEventsAsync(tripId);

        // Assert
        UnreadableSeatChange unreadable = Assert.Single(result.Unreadable);
        Assert.Equal(unreadableRequest, unreadable.Request);
        PublishedSeatEvent seatBooked = Assert.Single(events);
        Assert.Equal("SeatBooked", seatBooked.EventTypeAttribute);
    }

    private async Task<Guid> CreateTripAsync(params string[] availableSeatNos)
    {
        var tripId = Guid.NewGuid();
        await _store.PutTripAsync(tripId, Guid.NewGuid());
        foreach (string seatNo in availableSeatNos)
        {
            await _store.PutSeatAsync(tripId, seatNo, "Available", null);
        }

        return tripId;
    }

    private static string NewRequest()
    {
        return Guid.NewGuid().ToString("D");
    }

    // The change record every write slice puts on each seat it changes.
    private static Dictionary<string, string?> Record(string status, string reason, string request)
    {
        return new Dictionary<string, string?>
        {
            { "status", status },
            { "lastChangeReason", reason },
            { "lastChangeRequest", request },
            { "lastChangeCorrelationId", "correlation-" + request },
            { "lastChangedAt", PublishSeatChangesSeatStore.ChangedAt },
        };
    }

    private static List<string?> SeatNosOf(JsonElement data)
    {
        var seatNos = new List<string?>();
        foreach (JsonElement seatNo in data.GetProperty("seatNos").EnumerateArray())
        {
            seatNos.Add(seatNo.GetString());
        }

        return seatNos;
    }
}
