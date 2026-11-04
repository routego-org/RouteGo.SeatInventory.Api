namespace Api.Features.TripSeats.UpdateTripFares;

/// <summary>
/// CAP-SEAT-006: Fleet's FareChanged replaces the fare of every named seat, whatever its status; the
/// latest change wins, and a change for a trip not known yet waits for the trip. Seats are read back raw.
/// </summary>
public sealed class UpdateTripFaresTests : IClassFixture<UpdateTripFaresSeatStore>
{
    private readonly UpdateTripFaresSeatStore _store;

    public UpdateTripFaresTests(UpdateTripFaresSeatStore store)
    {
        _store = store;
    }

    // AC-1
    [Fact]
    public async Task HandleAsync_FaresChanged_ReplacesTheFareOfEverySeatWhateverItsStatus()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();
        await _store.PutScheduledTripAsync(tripId);
        await _store.PutAvailableSeatAsync(tripId, "L1", 900);
        await _store.PutHeldSeatAsync(tripId, "L2", bookingId, 900);
        ReceivedEvent fareChanged = UpdateTripFaresSeatStore.Event(
            "F1", UpdateTripFaresSeatStore.OccurredAt, UpdateTripFaresSeatStore.FareChanged(tripId, 1100, "L1", "L2"));

        // Act
        EventOutcome outcome = await _store.Handler!.HandleAsync(fareChanged, CancellationToken.None);

        // Assert
        Dictionary<string, string> trip = await _store.GetStoredTripAsync(tripId);
        Dictionary<string, string> l1 = await _store.GetStoredSeatAsync(tripId, "L1");
        Dictionary<string, string> l2 = await _store.GetStoredSeatAsync(tripId, "L2");
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        Assert.Equal("1100", l1["fare"]);
        Assert.Equal("1100", l2["fare"]);
        Assert.Equal("Held", l2["status"]);
        Assert.Equal(bookingId.ToString("D"), l2["bookingId"]);
        Assert.Equal("2026-10-22T08:00:00.0000000Z", trip["faresChangedAt"]);
    }

    // AC-1: the new fares record change reason FareChanged, which publishes no seat event
    [Fact]
    public async Task HandleAsync_FaresChanged_RecordsTheFareChangeAsOneChange()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        await _store.PutScheduledTripAsync(tripId);
        await _store.PutAvailableSeatAsync(tripId, "L1", 900);
        await _store.PutAvailableSeatAsync(tripId, "L2", 900);
        ReceivedEvent fareChanged = UpdateTripFaresSeatStore.Event(
            "F1", UpdateTripFaresSeatStore.OccurredAt, UpdateTripFaresSeatStore.FareChanged(tripId, 1100, "L1", "L2"));

        // Act
        await _store.Handler!.HandleAsync(fareChanged, CancellationToken.None);

        // Assert
        Dictionary<string, string> l1 = await _store.GetStoredSeatAsync(tripId, "L1");
        Dictionary<string, string> l2 = await _store.GetStoredSeatAsync(tripId, "L2");
        Assert.Equal("FareChanged", l1["lastChangeReason"]);
        Assert.Equal("fleet-correlation", l1["lastChangeCorrelationId"]);
        Assert.Equal(UpdateTripFaresSeatStore.Now.UtcDateTime.ToString("O"), l1["lastChangedAt"]);
        Assert.True(Guid.TryParse(l1["lastChangeRequest"], out _));
        Assert.Equal(l1["lastChangeRequest"], l2["lastChangeRequest"]);
    }

    // AC-2
    [Fact]
    public async Task HandleAsync_SameEventAgain_ChangesNothing()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        await _store.PutScheduledTripAsync(tripId);
        await _store.PutAvailableSeatAsync(tripId, "L1", 900);
        ReceivedEvent fareChanged = UpdateTripFaresSeatStore.Event(
            "F1", UpdateTripFaresSeatStore.OccurredAt, UpdateTripFaresSeatStore.FareChanged(tripId, 1100, "L1"));
        await _store.Handler!.HandleAsync(fareChanged, CancellationToken.None);
        Dictionary<string, string> before = await _store.GetStoredSeatAsync(tripId, "L1");

        // Act
        EventOutcome outcome = await _store.Handler.HandleAsync(fareChanged, CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        Assert.Equal(before, await _store.GetStoredSeatAsync(tripId, "L1"));
    }

    // AC-3
    [Fact]
    public async Task HandleAsync_OlderChangeArrivingLate_KeepsTheLatestFare()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        await _store.PutScheduledTripAsync(tripId);
        await _store.PutAvailableSeatAsync(tripId, "L1", 900);
        var tenOClock = new DateTimeOffset(2026, 10, 22, 10, 0, 0, TimeSpan.Zero);
        var nineOClock = new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);
        await _store.Handler!.HandleAsync(
            UpdateTripFaresSeatStore.Event("F10", tenOClock, UpdateTripFaresSeatStore.FareChanged(tripId, 1200, "L1")), CancellationToken.None);

        // Act
        EventOutcome outcome = await _store.Handler.HandleAsync(
            UpdateTripFaresSeatStore.Event("F09", nineOClock, UpdateTripFaresSeatStore.FareChanged(tripId, 1100, "L1")), CancellationToken.None);

        // Assert
        Dictionary<string, string> l1 = await _store.GetStoredSeatAsync(tripId, "L1");
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        Assert.Equal("1200", l1["fare"]);
    }

    // AC-4: a change for a trip not known yet is not applied, and waits
    [Fact]
    public async Task HandleAsync_TripNotKnownYet_PostponedAsTripUnknown()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        ReceivedEvent fareChanged = UpdateTripFaresSeatStore.Event(
            "F4", UpdateTripFaresSeatStore.OccurredAt, UpdateTripFaresSeatStore.FareChanged(tripId, 1100, "L1"));

        // Act
        EventOutcome outcome = await _store.Handler!.HandleAsync(fareChanged, CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Postponed, outcome.Kind);
        Assert.Equal("tripUnknown", outcome.Reason);
        Assert.Empty(await _store.GetStoredTripAsync(tripId));
    }

    // AC-4: once the trip has its seats, the retried change sets the new fares
    [Fact]
    public async Task HandleAsync_RetriedAfterTheTripArrived_SetsTheNewFares()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        ReceivedEvent fareChanged = UpdateTripFaresSeatStore.Event(
            "F4", UpdateTripFaresSeatStore.OccurredAt, UpdateTripFaresSeatStore.FareChanged(tripId, 1100, "L1"));
        await _store.Handler!.HandleAsync(fareChanged, CancellationToken.None);
        await _store.PutScheduledTripAsync(tripId);
        await _store.PutAvailableSeatAsync(tripId, "L1", 900);

        // Act
        EventOutcome outcome = await _store.Handler.HandleAsync(fareChanged, CancellationToken.None);

        // Assert
        Dictionary<string, string> l1 = await _store.GetStoredSeatAsync(tripId, "L1");
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        Assert.Equal("1100", l1["fare"]);
    }

    // AC-4 and AC-5: the postponed event stays on the queue; its redrive policy parks it after the last delivery
    [Fact]
    public async Task HandleWaitingEventsAsync_TripNotKnownYet_LeavesTheEventOnTheQueue()
    {
        // Arrange
        UpdateTripFaresQueues queues = await _store.CreateQueuesAsync("fare-changed-unknown-trip-tests");
        ReceivedEvent fareChanged = UpdateTripFaresSeatStore.Event(
            "F4", UpdateTripFaresSeatStore.OccurredAt, UpdateTripFaresSeatStore.FareChanged(Guid.NewGuid(), 1100, "L1"));
        await _store.SendEventAsync(queues.QueueUrl, fareChanged);

        // Act
        int appliedCount = await _store.WorkerOn(queues).HandleWaitingEventsAsync(CancellationToken.None);

        // Assert
        Assert.Equal(0, appliedCount);
        Assert.Equal(1, await _store.CountQueuedMessagesAsync(queues.QueueUrl));
        Assert.Equal(0, await _store.CountQueuedMessagesAsync(queues.DeadLetterQueueUrl));
    }

    // AC-6: no trip identifier, no seats
    [Theory]
    [InlineData("tripId")]
    [InlineData("seats")]
    public async Task HandleAsync_FieldMissing_RejectedAsInvalidEventData(string missingField)
    {
        // Arrange
        Dictionary<string, object?> data = UpdateTripFaresSeatStore.FareChanged(Guid.NewGuid(), 1100, "L1");
        data.Remove(missingField);

        // Act
        EventOutcome outcome = await _store.Handler!.HandleAsync(
            UpdateTripFaresSeatStore.Event("E9", UpdateTripFaresSeatStore.OccurredAt, data), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Rejected, outcome.Kind);
        Assert.Equal("invalidEventData", outcome.Reason);
    }

    // AC-6: a seat with no fare
    [Fact]
    public async Task HandleAsync_SeatWithoutFare_RejectedAndNoFareChanges()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        await _store.PutScheduledTripAsync(tripId);
        await _store.PutAvailableSeatAsync(tripId, "L1", 900);
        Dictionary<string, object?> data = UpdateTripFaresSeatStore.FareChanged(tripId, 1100, "L1");
        ((List<Dictionary<string, object?>>)data["seats"]!).Add(new Dictionary<string, object?> { { "seatNo", "L2" } });

        // Act
        EventOutcome outcome = await _store.Handler!.HandleAsync(
            UpdateTripFaresSeatStore.Event("E9", UpdateTripFaresSeatStore.OccurredAt, data), CancellationToken.None);

        // Assert
        Dictionary<string, string> l1 = await _store.GetStoredSeatAsync(tripId, "L1");
        Assert.Equal(EventOutcomeKind.Rejected, outcome.Kind);
        Assert.Equal("900", l1["fare"]);
    }

    // AC-6: a seat label the trip does not have
    [Fact]
    public async Task HandleAsync_SeatTheTripDoesNotHave_RejectedAndNoFareChanges()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        await _store.PutScheduledTripAsync(tripId);
        await _store.PutAvailableSeatAsync(tripId, "L1", 900);
        ReceivedEvent fareChanged = UpdateTripFaresSeatStore.Event(
            "E9", UpdateTripFaresSeatStore.OccurredAt, UpdateTripFaresSeatStore.FareChanged(tripId, 1100, "L1", "Z9"));

        // Act
        EventOutcome outcome = await _store.Handler!.HandleAsync(fareChanged, CancellationToken.None);

        // Assert
        Dictionary<string, string> l1 = await _store.GetStoredSeatAsync(tripId, "L1");
        Assert.Equal(EventOutcomeKind.Rejected, outcome.Kind);
        Assert.Equal($"trip {tripId} has no seats Z9", outcome.Problem);
        Assert.Equal("900", l1["fare"]);
    }

    // AC-6: an event that can never be applied is not retried — it is set aside at once
    [Fact]
    public async Task HandleWaitingEventsAsync_UnusableData_SetsTheEventAsideOnTheDeadLetterQueue()
    {
        // Arrange
        UpdateTripFaresQueues queues = await _store.CreateQueuesAsync("fare-changed-unusable-tests");
        Dictionary<string, object?> data = UpdateTripFaresSeatStore.FareChanged(Guid.NewGuid(), 1100, "L1");
        data.Remove("tripId");
        await _store.SendEventAsync(queues.QueueUrl, UpdateTripFaresSeatStore.Event("E9", UpdateTripFaresSeatStore.OccurredAt, data));

        // Act
        int appliedCount = await _store.WorkerOn(queues).HandleWaitingEventsAsync(CancellationToken.None);

        // Assert
        Assert.Equal(0, appliedCount);
        Assert.Equal(0, await _store.CountQueuedMessagesAsync(queues.QueueUrl));
        Assert.Equal(1, await _store.CountQueuedMessagesAsync(queues.DeadLetterQueueUrl));
    }
}
