namespace Api.Features.Holds.ExpireHold;

/// <summary>
/// CAP-HOLD-006: when a hold's timer fires, its seats are freed if — and only if — they are still Held
/// by that booking with that hold expiry. Seats are read back raw, because the change record on each
/// freed seat is what the seat-change stream turns into SeatReleased with reason HoldExpired.
/// </summary>
public sealed class ExpireHoldTests : IClassFixture<ExpireHoldSeatStore>
{
    private readonly ExpireHoldSeatStore _store;

    public ExpireHoldTests(ExpireHoldSeatStore store)
    {
        _store = store;
    }

    // AC-1
    [Fact]
    public async Task HandleAsync_UnpaidHoldAtItsExpiry_FreesItsSeatsTogetherWithReasonHoldExpired()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();
        await _store.PutTripAsync(tripId, false);
        await _store.PutHeldSeatAsync(tripId, "3A", bookingId, ExpireHoldSeatStore.HoldExpiresAt);
        await _store.PutHeldSeatAsync(tripId, "3B", bookingId, ExpireHoldSeatStore.HoldExpiresAt);

        // Act
        ExpireHoldResult result = await _store.Handler!.HandleAsync(Timer(tripId, bookingId), CancellationToken.None);

        // Assert
        Dictionary<string, string> seat3A = await _store.GetStoredSeatAsync(tripId, "3A");
        Dictionary<string, string> seat3B = await _store.GetStoredSeatAsync(tripId, "3B");
        Assert.Equal(new[] { "3A", "3B" }, result.ReleasedSeatNos);
        Assert.Equal("Available", seat3A["status"]);
        Assert.Equal("HoldExpired", seat3A["lastChangeReason"]);
        Assert.Equal("hold-correlation", seat3A["lastChangeCorrelationId"]);
        Assert.Equal(ExpireHoldSeatStore.Now.UtcDateTime.ToString("O"), seat3A["lastChangedAt"]);
        Assert.True(Guid.TryParse(seat3A["lastChangeRequest"], out _));
        Assert.Equal(seat3A["lastChangeRequest"], seat3B["lastChangeRequest"]);
        Assert.False(seat3A.ContainsKey("bookingId"));
        Assert.False(seat3A.ContainsKey("holdExpiresAt"));
        Assert.Equal("Available", seat3B["status"]);
    }

    // AC-2
    [Fact]
    public async Task HandleAsync_HoldAlreadyPaid_LeavesTheSeatsBooked()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();
        await _store.PutTripAsync(tripId, false);
        await _store.PutBookedSeatAsync(tripId, "4A", bookingId);

        // Act
        ExpireHoldResult result = await _store.Handler!.HandleAsync(Timer(tripId, bookingId), CancellationToken.None);

        // Assert
        Dictionary<string, string> seat = await _store.GetStoredSeatAsync(tripId, "4A");
        Assert.Empty(result.ReleasedSeatNos);
        Assert.Equal("Booked", seat["status"]);
        Assert.Equal(bookingId.ToString("D"), seat["bookingId"]);
    }

    // AC-3
    [Fact]
    public async Task HandleAsync_HoldReleasedAndSeatHeldByAnotherBooking_LeavesTheOtherHoldAlone()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();
        var otherBookingId = Guid.NewGuid();
        await _store.PutTripAsync(tripId, false);
        await _store.PutHeldSeatAsync(tripId, "5A", otherBookingId, ExpireHoldSeatStore.HoldExpiresAt);

        // Act
        ExpireHoldResult result = await _store.Handler!.HandleAsync(Timer(tripId, bookingId), CancellationToken.None);

        // Assert
        Dictionary<string, string> seat = await _store.GetStoredSeatAsync(tripId, "5A");
        Assert.Empty(result.ReleasedSeatNos);
        Assert.Equal("Held", seat["status"]);
        Assert.Equal(otherBookingId.ToString("D"), seat["bookingId"]);
    }

    // AC-4
    [Fact]
    public async Task HandleAsync_StaleTimerOfAHoldMadeAgain_LeavesTheNewerHoldAlone()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();
        const string newerExpiry = "2026-10-24T10:15:00.0000000Z";
        await _store.PutTripAsync(tripId, false);
        await _store.PutHeldSeatAsync(tripId, "6A", bookingId, newerExpiry);

        // Act
        ExpireHoldResult result = await _store.Handler!.HandleAsync(Timer(tripId, bookingId), CancellationToken.None);

        // Assert
        Dictionary<string, string> seat = await _store.GetStoredSeatAsync(tripId, "6A");
        Assert.Empty(result.ReleasedSeatNos);
        Assert.Equal("Held", seat["status"]);
        Assert.Equal(newerExpiry, seat["holdExpiresAt"]);
    }

    // AC-6
    [Fact]
    public async Task HandleAsync_CancelledTrip_BlocksTheSeatsInsteadOfFreeingThem()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();
        await _store.PutTripAsync(tripId, true);
        await _store.PutHeldSeatAsync(tripId, "7A", bookingId, ExpireHoldSeatStore.HoldExpiresAt);

        // Act
        ExpireHoldResult result = await _store.Handler!.HandleAsync(Timer(tripId, bookingId), CancellationToken.None);

        // Assert
        Dictionary<string, string> seat = await _store.GetStoredSeatAsync(tripId, "7A");
        Assert.Equal(new[] { "7A" }, result.ReleasedSeatNos);
        Assert.Equal("Blocked", seat["status"]);
        Assert.Equal("TripCancelled", seat["blockReason"]);
        Assert.Equal("HoldExpired", seat["lastChangeReason"]);
    }

    // AC-7
    [Fact]
    public async Task HandleAsync_TimerDeliveredTwice_SecondChangesNothing()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();
        await _store.PutTripAsync(tripId, false);
        await _store.PutHeldSeatAsync(tripId, "8A", bookingId, ExpireHoldSeatStore.HoldExpiresAt);
        await _store.Handler!.HandleAsync(Timer(tripId, bookingId), CancellationToken.None);
        Dictionary<string, string> afterFirst = await _store.GetStoredSeatAsync(tripId, "8A");

        // Act
        ExpireHoldResult second = await _store.Handler.HandleAsync(Timer(tripId, bookingId), CancellationToken.None);

        // Assert
        Dictionary<string, string> afterSecond = await _store.GetStoredSeatAsync(tripId, "8A");
        Assert.Empty(second.ReleasedSeatNos);
        Assert.Equal(afterFirst["lastChangeRequest"], afterSecond["lastChangeRequest"]);
    }

    // AC-5: a timer waiting on the queue is handled whenever the worker runs, and then removed
    [Fact]
    public async Task ExpireDueHoldsAsync_TimerWaitingOnTheQueue_FreesTheSeatsAndRemovesTheTimer()
    {
        // Arrange
        string queueUrl = await _store.CreateQueueAsync("hold-expiry-handled-tests");
        var tripId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();
        await _store.PutTripAsync(tripId, false);
        await _store.PutHeldSeatAsync(tripId, "9A", bookingId, ExpireHoldSeatStore.HoldExpiresAt);
        await _store.SendTimerAsync(queueUrl, Timer(tripId, bookingId));

        // Act
        int handledCount = await _store.WorkerOn(queueUrl).ExpireDueHoldsAsync(CancellationToken.None);

        // Assert
        Dictionary<string, string> seat = await _store.GetStoredSeatAsync(tripId, "9A");
        Assert.Equal(1, handledCount);
        Assert.Equal("Available", seat["status"]);
        Assert.Equal(0, await _store.CountQueuedTimersAsync(queueUrl));
    }

    // AC-8: a timer that fails stays on the queue for the next delivery, and the seats stay Held
    [Fact]
    public async Task ExpireDueHoldsAsync_SeatStoreUnavailable_LeavesTheTimerOnTheQueue()
    {
        // Arrange
        string queueUrl = await _store.CreateQueueAsync("hold-expiry-failing-tests");
        var tripId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();
        await _store.PutTripAsync(tripId, false);
        await _store.PutHeldSeatAsync(tripId, "10A", bookingId, ExpireHoldSeatStore.HoldExpiresAt);
        await _store.SendTimerAsync(queueUrl, Timer(tripId, bookingId));

        // Act
        int handledCount = await _store.WorkerWithSeatStoreDownOn(queueUrl).ExpireDueHoldsAsync(CancellationToken.None);

        // Assert
        Dictionary<string, string> seat = await _store.GetStoredSeatAsync(tripId, "10A");
        Assert.Equal(0, handledCount);
        Assert.Equal("Held", seat["status"]);
        Assert.Equal(1, await _store.CountQueuedTimersAsync(queueUrl));
    }

    private static HoldExpiryTimerMessage Timer(Guid tripId, Guid bookingId)
    {
        return new HoldExpiryTimerMessage
        {
            TripId = tripId,
            BookingId = bookingId,
            HoldExpiresAt = ExpireHoldSeatStore.HoldExpiresAt,
            SeatNos = new List<string>(),
            CorrelationId = "hold-correlation",
        };
    }
}
