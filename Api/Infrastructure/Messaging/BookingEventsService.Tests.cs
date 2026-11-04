namespace Api.Infrastructure.Messaging;

/// <summary>
/// The one booking queue serves CAP-HOLD-007 and CAP-HOLD-008: each event reaches its own slice, an event
/// that can never be applied is set aside at once, and one that fails stays for the next delivery.
/// </summary>
public sealed class BookingEventsServiceTests : IClassFixture<BookingEventsSeatStore>
{
    private readonly BookingEventsSeatStore _store;

    public BookingEventsServiceTests(BookingEventsSeatStore store)
    {
        _store = store;
    }

    [Fact]
    public async Task HandleWaitingEventsAsync_BookingFailedAndBookingCancelled_EachFreesItsOwnBookingsSeats()
    {
        // Arrange
        BookingEventsQueues queues = await _store.CreateQueuesAsync("booking-events-routed-tests");
        var tripId = Guid.NewGuid();
        var failedBookingId = Guid.NewGuid();
        var cancelledBookingId = Guid.NewGuid();
        await _store.PutTripAsync(tripId);
        await _store.PutOwnedSeatAsync(tripId, "3A", "Held", failedBookingId);
        await _store.PutOwnedSeatAsync(tripId, "4C", "Booked", cancelledBookingId);
        await _store.SendEventAsync(queues.QueueUrl, BookingEventsSeatStore.Event("BookingFailed", failedBookingId, tripId));
        await _store.SendEventAsync(queues.QueueUrl, BookingEventsSeatStore.Event("BookingCancelled", cancelledBookingId, tripId));

        // Act
        int appliedCount = await _store.WorkerOn(queues).HandleWaitingEventsAsync(CancellationToken.None);

        // Assert
        Assert.Equal(2, appliedCount);
        Assert.Equal("Available", await _store.GetSeatStatusAsync(tripId, "3A"));
        Assert.Equal("Available", await _store.GetSeatStatusAsync(tripId, "4C"));
        Assert.Equal(0, await _store.CountQueuedMessagesAsync(queues.QueueUrl));
    }

    // AC-8: unusable data is not retried — it is set aside at once
    [Fact]
    public async Task HandleWaitingEventsAsync_NoBookingId_SetsTheEventAsideOnTheDeadLetterQueue()
    {
        // Arrange
        BookingEventsQueues queues = await _store.CreateQueuesAsync("booking-events-unusable-tests");
        var data = new Dictionary<string, object?> { { "tripId", Guid.NewGuid() } };
        await _store.SendEventAsync(queues.QueueUrl, BookingEventsSeatStore.EventWith("BookingCancelled", data));

        // Act
        int appliedCount = await _store.WorkerOn(queues).HandleWaitingEventsAsync(CancellationToken.None);

        // Assert
        Assert.Equal(0, appliedCount);
        Assert.Equal(0, await _store.CountQueuedMessagesAsync(queues.QueueUrl));
        Assert.Equal(1, await _store.CountQueuedMessagesAsync(queues.DeadLetterQueueUrl));
    }

    // AC-8: a temporary failure leaves the event on the queue; its redrive policy parks it after the last delivery
    [Fact]
    public async Task HandleWaitingEventsAsync_SeatStoreUnavailable_LeavesTheEventOnTheQueue()
    {
        // Arrange
        BookingEventsQueues queues = await _store.CreateQueuesAsync("booking-events-failing-tests");
        await _store.SendEventAsync(queues.QueueUrl, BookingEventsSeatStore.Event("BookingFailed", Guid.NewGuid(), Guid.NewGuid()));

        // Act
        int appliedCount = await _store.WorkerWithSeatStoreDownOn(queues).HandleWaitingEventsAsync(CancellationToken.None);

        // Assert
        Assert.Equal(0, appliedCount);
        Assert.Equal(1, await _store.CountQueuedMessagesAsync(queues.QueueUrl));
        Assert.Equal(0, await _store.CountQueuedMessagesAsync(queues.DeadLetterQueueUrl));
    }

    // An event the queue's subscription should have kept out is set aside, never applied
    [Fact]
    public async Task HandleWaitingEventsAsync_EventOfAnotherType_SetsItAsideOnTheDeadLetterQueue()
    {
        // Arrange
        BookingEventsQueues queues = await _store.CreateQueuesAsync("booking-events-wrong-type-tests");
        await _store.SendEventAsync(queues.QueueUrl, BookingEventsSeatStore.Event("BookingConfirmed", Guid.NewGuid(), Guid.NewGuid()));

        // Act
        int appliedCount = await _store.WorkerOn(queues).HandleWaitingEventsAsync(CancellationToken.None);

        // Assert
        Assert.Equal(0, appliedCount);
        Assert.Equal(1, await _store.CountQueuedMessagesAsync(queues.DeadLetterQueueUrl));
    }
}
