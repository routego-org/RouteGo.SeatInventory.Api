namespace Api.Features.TripSeats.CreateTripSeats;

/// <summary>
/// CAP-SEAT-005: Fleet's TripScheduled creates Seat Inventory's record of the trip and every one of its
/// seats, once. Trips and seats are read back raw; the seat map is read through the real endpoint.
/// </summary>
public sealed class CreateTripSeatsTests : IClassFixture<CreateTripSeatsSeatStore>
{
    private readonly CreateTripSeatsSeatStore _store;

    public CreateTripSeatsTests(CreateTripSeatsSeatStore store)
    {
        _store = store;
    }

    // AC-1
    [Fact]
    public async Task HandleAsync_NewTrip_CreatesTheTripAndEverySeatAvailable()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        ReceivedEvent tripScheduled = CreateTripSeatsSeatStore.Event("E1", CreateTripSeatsSeatStore.TripScheduled(tripId, 30, 1100));

        // Act
        EventOutcome outcome = await _store.Handler!.HandleAsync(tripScheduled, CancellationToken.None);

        // Assert
        Dictionary<string, string> trip = await _store.GetStoredTripAsync(tripId);
        List<Dictionary<string, string>> seats = await _store.ListStoredSeatsAsync(tripId);
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        Assert.Equal(CreateTripSeatsSeatStore.OperatorId.ToString("D"), trip["operatorId"]);
        Assert.Equal("2026-10-24T16:30:00.0000000Z", trip["departureAt"]);
        Assert.Equal("False", trip["isCancelled"]);
        Assert.Equal(30, seats.Count);
        Assert.All(seats, seat => Assert.Equal("Available", seat["status"]));
    }

    // AC-1: each seat takes its position, class and fare from the event, and records its creation
    [Fact]
    public async Task HandleAsync_NewTrip_SeatHasItsPositionClassFareAndChangeRecord()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        ReceivedEvent tripScheduled = CreateTripSeatsSeatStore.Event("E1", CreateTripSeatsSeatStore.TripScheduled(tripId, 30, 1100));

        // Act
        await _store.Handler!.HandleAsync(tripScheduled, CancellationToken.None);

        // Assert
        Dictionary<string, string> l1 = await _store.GetStoredSeatAsync(tripId, "L1");
        Dictionary<string, string> l2 = await _store.GetStoredSeatAsync(tripId, "L2");
        Assert.Equal("Lower", l1["deck"]);
        Assert.Equal("1", l1["row"]);
        Assert.Equal("1", l1["column"]);
        Assert.Equal("Lower berth", l1["seatClass"]);
        Assert.Equal("1100", l1["fare"]);
        Assert.Equal("Created", l1["lastChangeReason"]);
        Assert.Equal("fleet-correlation", l1["lastChangeCorrelationId"]);
        Assert.Equal(CreateTripSeatsSeatStore.Now.UtcDateTime.ToString("O"), l1["lastChangedAt"]);
        Assert.True(Guid.TryParse(l1["lastChangeRequest"], out _));
        Assert.Equal(l1["lastChangeRequest"], l2["lastChangeRequest"]);
    }

    // AC-1: the seat map of the new trip can be read
    [Fact]
    public async Task HandleAsync_NewTrip_SeatMapCanBeRead()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        ReceivedEvent tripScheduled = CreateTripSeatsSeatStore.Event("E1", CreateTripSeatsSeatStore.TripScheduled(tripId, 30, 1100));
        await _store.Handler!.HandleAsync(tripScheduled, CancellationToken.None);

        // Act
        HttpResponseMessage response = await _store.Client!.GetAsync($"/trips/{tripId}/seats");

        // Assert
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(30, body.RootElement.GetProperty("seats").GetArrayLength());
        Assert.False(body.RootElement.GetProperty("isCancelled").GetBoolean());
    }

    // AC-2
    [Fact]
    public async Task HandleAsync_SameEventAgainAfterASeatWasHeld_KeepsTheHoldAndTheSeatCount()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();
        ReceivedEvent tripScheduled = CreateTripSeatsSeatStore.Event("E1", CreateTripSeatsSeatStore.TripScheduled(tripId, 30, 1100));
        await _store.Handler!.HandleAsync(tripScheduled, CancellationToken.None);
        await _store.HoldSeatAsync(tripId, "L3", bookingId);

        // Act
        EventOutcome outcome = await _store.Handler.HandleAsync(tripScheduled, CancellationToken.None);

        // Assert
        Dictionary<string, string> l3 = await _store.GetStoredSeatAsync(tripId, "L3");
        List<Dictionary<string, string>> seats = await _store.ListStoredSeatsAsync(tripId);
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        Assert.Equal(30, seats.Count);
        Assert.Equal("Held", l3["status"]);
        Assert.Equal(bookingId.ToString("D"), l3["bookingId"]);
    }

    // AC-3
    [Fact]
    public async Task HandleAsync_DifferentEventForAKnownTrip_ChangesNothing()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        ReceivedEvent first = CreateTripSeatsSeatStore.Event("E1", CreateTripSeatsSeatStore.TripScheduled(tripId, 30, 1100));
        ReceivedEvent other = CreateTripSeatsSeatStore.Event("E2", CreateTripSeatsSeatStore.TripScheduled(tripId, 32, 1500));
        await _store.Handler!.HandleAsync(first, CancellationToken.None);
        Dictionary<string, string> l1Before = await _store.GetStoredSeatAsync(tripId, "L1");

        // Act
        EventOutcome outcome = await _store.Handler.HandleAsync(other, CancellationToken.None);

        // Assert
        Dictionary<string, string> l1After = await _store.GetStoredSeatAsync(tripId, "L1");
        List<Dictionary<string, string>> seats = await _store.ListStoredSeatsAsync(tripId);
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        Assert.Equal(30, seats.Count);
        Assert.Equal(l1Before, l1After);
    }

    // AC-4
    [Fact]
    public async Task HandleAsync_CancellationArrivedFirst_CreatesEverySeatBlockedForTheCancellation()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        await _store.PutCancelledTripAsync(tripId);
        ReceivedEvent tripScheduled = CreateTripSeatsSeatStore.Event("E1", CreateTripSeatsSeatStore.TripScheduled(tripId, 4, 900));

        // Act
        await _store.Handler!.HandleAsync(tripScheduled, CancellationToken.None);

        // Assert
        Dictionary<string, string> trip = await _store.GetStoredTripAsync(tripId);
        List<Dictionary<string, string>> seats = await _store.ListStoredSeatsAsync(tripId);
        Assert.Equal("True", trip["isCancelled"]);
        Assert.Equal(4, seats.Count);
        Assert.All(seats, seat => Assert.Equal("Blocked", seat["status"]));
        Assert.All(seats, seat => Assert.Equal("TripCancelled", seat["blockReason"]));
    }

    // AC-5: the trip-level fields
    [Theory]
    [InlineData("tripId")]
    [InlineData("operatorId")]
    [InlineData("departureUtc")]
    [InlineData("seats")]
    public async Task HandleAsync_TripFieldMissing_RejectedAndNothingCreated(string missingField)
    {
        // Arrange
        var tripId = Guid.NewGuid();
        Dictionary<string, object?> data = CreateTripSeatsSeatStore.TripScheduled(tripId, 4, 900);
        data.Remove(missingField);

        // Act
        EventOutcome outcome = await _store.Handler!.HandleAsync(CreateTripSeatsSeatStore.Event("E9", data), CancellationToken.None);

        // Assert
        Dictionary<string, string> trip = await _store.GetStoredTripAsync(tripId);
        Assert.Equal(EventOutcomeKind.Rejected, outcome.Kind);
        Assert.Equal("invalidEventData", outcome.Reason);
        Assert.Empty(trip);
    }

    // AC-5: a seat with no class or fare
    [Theory]
    [InlineData("class")]
    [InlineData("fare")]
    public async Task HandleAsync_SeatFieldMissing_RejectedAndNothingCreated(string missingField)
    {
        // Arrange
        var tripId = Guid.NewGuid();
        Dictionary<string, object?> data = CreateTripSeatsSeatStore.TripScheduled(tripId, 4, 900);
        Dictionary<string, object?> brokenSeat = CreateTripSeatsSeatStore.SeatData("U1", 1, 1, 900);
        brokenSeat.Remove(missingField);
        ((List<Dictionary<string, object?>>)data["seats"]!).Add(brokenSeat);

        // Act
        EventOutcome outcome = await _store.Handler!.HandleAsync(CreateTripSeatsSeatStore.Event("E9", data), CancellationToken.None);

        // Assert
        Dictionary<string, string> trip = await _store.GetStoredTripAsync(tripId);
        Assert.Equal(EventOutcomeKind.Rejected, outcome.Kind);
        Assert.Empty(trip);
    }

    // AC-5: two seats with the same label
    [Fact]
    public async Task HandleAsync_TwoSeatsWithTheSameLabel_RejectedAndNothingCreated()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        Dictionary<string, object?> data = CreateTripSeatsSeatStore.TripScheduled(tripId, 4, 900);
        ((List<Dictionary<string, object?>>)data["seats"]!).Add(CreateTripSeatsSeatStore.SeatData("L1", 2, 1, 900));

        // Act
        EventOutcome outcome = await _store.Handler!.HandleAsync(CreateTripSeatsSeatStore.Event("E9", data), CancellationToken.None);

        // Assert
        Dictionary<string, string> trip = await _store.GetStoredTripAsync(tripId);
        Assert.Equal(EventOutcomeKind.Rejected, outcome.Kind);
        Assert.Equal("two seats have the seatNo L1", outcome.Problem);
        Assert.Empty(trip);
    }

    // AC-5: an event that can never be applied is not retried — it is set aside at once
    [Fact]
    public async Task HandleWaitingEventsAsync_UnusableData_SetsTheEventAsideOnTheDeadLetterQueue()
    {
        // Arrange
        CreateTripSeatsQueues queues = await _store.CreateQueuesAsync("trip-scheduled-unusable-tests");
        Dictionary<string, object?> data = CreateTripSeatsSeatStore.TripScheduled(Guid.NewGuid(), 4, 900);
        data.Remove("tripId");
        await _store.SendEventAsync(queues.QueueUrl, CreateTripSeatsSeatStore.Event("E9", data));

        // Act
        int appliedCount = await _store.WorkerOn(queues).HandleWaitingEventsAsync(CancellationToken.None);

        // Assert
        Assert.Equal(0, appliedCount);
        Assert.Equal(0, await _store.CountQueuedMessagesAsync(queues.QueueUrl));
        Assert.Equal(1, await _store.CountQueuedMessagesAsync(queues.DeadLetterQueueUrl));
    }

    // AC-6: a temporary failure leaves the event on the queue for the next delivery
    [Fact]
    public async Task HandleWaitingEventsAsync_SeatStoreUnavailable_LeavesTheEventOnTheQueue()
    {
        // Arrange
        CreateTripSeatsQueues queues = await _store.CreateQueuesAsync("trip-scheduled-failing-tests");
        ReceivedEvent tripScheduled = CreateTripSeatsSeatStore.Event("E1", CreateTripSeatsSeatStore.TripScheduled(Guid.NewGuid(), 4, 900));
        await _store.SendEventAsync(queues.QueueUrl, tripScheduled);

        // Act
        int appliedCount = await _store.WorkerWithSeatStoreDownOn(queues).HandleWaitingEventsAsync(CancellationToken.None);

        // Assert
        Assert.Equal(0, appliedCount);
        Assert.Equal(1, await _store.CountQueuedMessagesAsync(queues.QueueUrl));
        Assert.Equal(0, await _store.CountQueuedMessagesAsync(queues.DeadLetterQueueUrl));
    }

    // AC-7: an announcement waiting on the queue is applied whenever the worker runs, and then removed
    [Fact]
    public async Task HandleWaitingEventsAsync_EventWaitingOnTheQueue_CreatesTheSeatsAndRemovesTheEvent()
    {
        // Arrange
        CreateTripSeatsQueues queues = await _store.CreateQueuesAsync("trip-scheduled-waiting-tests");
        var tripId = Guid.NewGuid();
        await _store.SendEventAsync(queues.QueueUrl, CreateTripSeatsSeatStore.Event("E3", CreateTripSeatsSeatStore.TripScheduled(tripId, 4, 900)));

        // Act
        int appliedCount = await _store.WorkerOn(queues).HandleWaitingEventsAsync(CancellationToken.None);

        // Assert
        List<Dictionary<string, string>> seats = await _store.ListStoredSeatsAsync(tripId);
        Assert.Equal(1, appliedCount);
        Assert.Equal(4, seats.Count);
        Assert.Equal(0, await _store.CountQueuedMessagesAsync(queues.QueueUrl));
    }

    // An event of another type on this queue means its subscription filter is wrong: set aside, never applied
    [Fact]
    public async Task HandleWaitingEventsAsync_EventOfAnotherType_SetsItAsideOnTheDeadLetterQueue()
    {
        // Arrange
        CreateTripSeatsQueues queues = await _store.CreateQueuesAsync("trip-scheduled-wrong-type-tests");
        var tripId = Guid.NewGuid();
        ReceivedEvent fareChanged = CreateTripSeatsSeatStore.Event("F1", CreateTripSeatsSeatStore.TripScheduled(tripId, 4, 900)) with
        {
            EventType = "FareChanged",
        };
        await _store.SendEventAsync(queues.QueueUrl, fareChanged);

        // Act
        int appliedCount = await _store.WorkerOn(queues).HandleWaitingEventsAsync(CancellationToken.None);

        // Assert
        Dictionary<string, string> trip = await _store.GetStoredTripAsync(tripId);
        Assert.Equal(0, appliedCount);
        Assert.Empty(trip);
        Assert.Equal(1, await _store.CountQueuedMessagesAsync(queues.DeadLetterQueueUrl));
    }
}
