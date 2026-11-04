namespace Api.Features.TripSeats.CancelTrip;

/// <summary>
/// CAP-SEAT-007: Fleet's TripCancelled marks the trip cancelled for good and blocks its free seats,
/// leaving held and booked seats to Booking. Trips and seats are read back raw, because the change record
/// on each blocked seat is what keeps the seat-change stream silent about it.
/// </summary>
public sealed class CancelTripTests : IClassFixture<CancelTripSeatStore>
{
    private readonly CancelTripSeatStore _store;

    public CancelTripTests(CancelTripSeatStore store)
    {
        _store = store;
    }

    // AC-1
    [Fact]
    public async Task HandleAsync_TripWithFreeHeldAndBookedSeats_BlocksOnlyTheFreeSeats()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        var heldBy = Guid.NewGuid();
        var bookedBy = Guid.NewGuid();
        await _store.PutScheduledTripAsync(tripId);
        await _store.PutAvailableSeatAsync(tripId, "1A");
        await _store.PutBlockedSeatAsync(tripId, "L1", "Operator");
        await _store.PutOwnedSeatAsync(tripId, "3A", "Held", heldBy);
        await _store.PutOwnedSeatAsync(tripId, "4C", "Booked", bookedBy);

        // Act
        EventOutcome outcome = await _store.Handler!.HandleAsync(
            CancelTripSeatStore.Event("C1", CancelTripSeatStore.TripCancelled(tripId)), CancellationToken.None);

        // Assert
        Dictionary<string, string> trip = await _store.GetStoredTripAsync(tripId);
        Dictionary<string, string> seat1A = await _store.GetStoredSeatAsync(tripId, "1A");
        Dictionary<string, string> seatL1 = await _store.GetStoredSeatAsync(tripId, "L1");
        Dictionary<string, string> seat3A = await _store.GetStoredSeatAsync(tripId, "3A");
        Dictionary<string, string> seat4C = await _store.GetStoredSeatAsync(tripId, "4C");
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        Assert.Equal("True", trip["isCancelled"]);
        Assert.Equal(CancelTripSeatStore.Now.UtcDateTime.ToString("O"), trip["cancelledAt"]);
        Assert.Equal("Blocked", seat1A["status"]);
        Assert.Equal("TripCancelled", seat1A["blockReason"]);
        Assert.Equal("Blocked", seatL1["status"]);
        Assert.Equal("TripCancelled", seatL1["blockReason"]);
        Assert.Equal("Held", seat3A["status"]);
        Assert.Equal(heldBy.ToString("D"), seat3A["bookingId"]);
        Assert.Equal("Booked", seat4C["status"]);
        Assert.Equal(bookedBy.ToString("D"), seat4C["bookingId"]);
    }

    // AC-1: the blocks record change reason TripCancelled, which publishes no seat event
    [Fact]
    public async Task HandleAsync_FreeSeatsBlocked_RecordTheCancellationAsOneChange()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        await _store.PutScheduledTripAsync(tripId);
        await _store.PutAvailableSeatAsync(tripId, "1A");
        await _store.PutBlockedSeatAsync(tripId, "L1", "Operator");

        // Act
        await _store.Handler!.HandleAsync(CancelTripSeatStore.Event("C1", CancelTripSeatStore.TripCancelled(tripId)), CancellationToken.None);

        // Assert
        Dictionary<string, string> seat1A = await _store.GetStoredSeatAsync(tripId, "1A");
        Dictionary<string, string> seatL1 = await _store.GetStoredSeatAsync(tripId, "L1");
        Assert.Equal("TripCancelled", seat1A["lastChangeReason"]);
        Assert.Equal("fleet-correlation", seat1A["lastChangeCorrelationId"]);
        Assert.Equal(CancelTripSeatStore.Now.UtcDateTime.ToString("O"), seat1A["lastChangedAt"]);
        Assert.True(Guid.TryParse(seat1A["lastChangeRequest"], out _));
        Assert.Equal(seat1A["lastChangeRequest"], seatL1["lastChangeRequest"]);
        Assert.False(seatL1.ContainsKey("lastChangedBy"));
    }

    // AC-4
    [Fact]
    public async Task HandleAsync_CancellationDeliveredAgain_ChangesNothing()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        await _store.PutScheduledTripAsync(tripId);
        await _store.PutAvailableSeatAsync(tripId, "1A");
        ReceivedEvent tripCancelled = CancelTripSeatStore.Event("C1", CancelTripSeatStore.TripCancelled(tripId));
        await _store.Handler!.HandleAsync(tripCancelled, CancellationToken.None);
        await _store.PutBlockedSeatAsync(tripId, "3A", "TripCancelled");
        Dictionary<string, string> seat1ABefore = await _store.GetStoredSeatAsync(tripId, "1A");
        Dictionary<string, string> seat3ABefore = await _store.GetStoredSeatAsync(tripId, "3A");

        // Act
        EventOutcome outcome = await _store.Handler.HandleAsync(tripCancelled, CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        Assert.Equal(seat1ABefore, await _store.GetStoredSeatAsync(tripId, "1A"));
        Assert.Equal(seat3ABefore, await _store.GetStoredSeatAsync(tripId, "3A"));
    }

    // AC-5
    [Fact]
    public async Task HandleAsync_TripNotKnownYet_RemembersItAsCancelledWithoutSeats()
    {
        // Arrange
        var tripId = Guid.NewGuid();

        // Act
        EventOutcome outcome = await _store.Handler!.HandleAsync(
            CancelTripSeatStore.Event("C2", CancelTripSeatStore.TripCancelled(tripId)), CancellationToken.None);

        // Assert
        Dictionary<string, string> trip = await _store.GetStoredTripAsync(tripId);
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        Assert.Equal("True", trip["isCancelled"]);
        Assert.False(trip.ContainsKey("departureAt"));
        Assert.False(trip.ContainsKey("seatsCreatedAt"));
    }

    // AC-6
    [Fact]
    public async Task HandleAsync_NoTripId_RejectedAndNothingChanges()
    {
        // Arrange
        var data = new Dictionary<string, object?> { { "reason", "Bus breakdown" } };

        // Act
        EventOutcome outcome = await _store.Handler!.HandleAsync(CancelTripSeatStore.Event("E9", data), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Rejected, outcome.Kind);
        Assert.Equal("invalidEventData", outcome.Reason);
    }

    // AC-6: an event that can never be applied is not retried — it is set aside at once
    [Fact]
    public async Task HandleWaitingEventsAsync_NoTripId_SetsTheEventAsideOnTheDeadLetterQueue()
    {
        // Arrange
        CancelTripQueues queues = await _store.CreateQueuesAsync("trip-cancelled-unusable-tests");
        var data = new Dictionary<string, object?> { { "reason", "Bus breakdown" } };
        await _store.SendEventAsync(queues.QueueUrl, CancelTripSeatStore.Event("E9", data));

        // Act
        int appliedCount = await _store.WorkerOn(queues).HandleWaitingEventsAsync(CancellationToken.None);

        // Assert
        Assert.Equal(0, appliedCount);
        Assert.Equal(0, await _store.CountQueuedMessagesAsync(queues.QueueUrl));
        Assert.Equal(1, await _store.CountQueuedMessagesAsync(queues.DeadLetterQueueUrl));
    }

    // AC-7: a temporary failure leaves the event on the queue for the next delivery
    [Fact]
    public async Task HandleWaitingEventsAsync_SeatStoreUnavailable_LeavesTheEventOnTheQueue()
    {
        // Arrange
        CancelTripQueues queues = await _store.CreateQueuesAsync("trip-cancelled-failing-tests");
        await _store.SendEventAsync(queues.QueueUrl, CancelTripSeatStore.Event("C3", CancelTripSeatStore.TripCancelled(Guid.NewGuid())));

        // Act
        int appliedCount = await _store.WorkerWithSeatStoreDownOn(queues).HandleWaitingEventsAsync(CancellationToken.None);

        // Assert
        Assert.Equal(0, appliedCount);
        Assert.Equal(1, await _store.CountQueuedMessagesAsync(queues.QueueUrl));
        Assert.Equal(0, await _store.CountQueuedMessagesAsync(queues.DeadLetterQueueUrl));
    }

    // AC-7: once the store is back, the replayed event completes the cancellation
    [Fact]
    public async Task HandleWaitingEventsAsync_EventWaitingOnTheQueue_CancelsTheTripAndRemovesTheEvent()
    {
        // Arrange
        CancelTripQueues queues = await _store.CreateQueuesAsync("trip-cancelled-waiting-tests");
        var tripId = Guid.NewGuid();
        await _store.PutScheduledTripAsync(tripId);
        await _store.PutAvailableSeatAsync(tripId, "1A");
        await _store.SendEventAsync(queues.QueueUrl, CancelTripSeatStore.Event("C4", CancelTripSeatStore.TripCancelled(tripId)));

        // Act
        int appliedCount = await _store.WorkerOn(queues).HandleWaitingEventsAsync(CancellationToken.None);

        // Assert
        Dictionary<string, string> seat1A = await _store.GetStoredSeatAsync(tripId, "1A");
        Assert.Equal(1, appliedCount);
        Assert.Equal("Blocked", seat1A["status"]);
        Assert.Equal(0, await _store.CountQueuedMessagesAsync(queues.QueueUrl));
    }
}
