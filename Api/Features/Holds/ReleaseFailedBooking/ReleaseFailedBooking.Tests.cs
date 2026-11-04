namespace Api.Features.Holds.ReleaseFailedBooking;

/// <summary>
/// CAP-HOLD-007: Booking's BookingFailed frees the seats the booking still holds on the trip — never a
/// Booked one. Seats are read back raw, because the change record on each freed seat is what the
/// seat-change stream turns into SeatReleased with reason BookingFailed.
/// </summary>
public sealed class ReleaseFailedBookingTests : IClassFixture<ReleaseFailedBookingSeatStore>
{
    private readonly ReleaseFailedBookingSeatStore _store;

    public ReleaseFailedBookingTests(ReleaseFailedBookingSeatStore store)
    {
        _store = store;
    }

    // AC-1
    [Fact]
    public async Task HandleAsync_FailedBookingHoldsSeats_FreesThemTogetherWithReasonBookingFailed()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();
        await _store.PutTripAsync(tripId, false);
        await _store.PutOwnedSeatAsync(tripId, "3A", "Held", bookingId);
        await _store.PutOwnedSeatAsync(tripId, "3B", "Held", bookingId);

        // Act
        EventOutcome outcome = await _store.Handler!.HandleAsync(ReleaseFailedBookingSeatStore.Event(bookingId, tripId), CancellationToken.None);

        // Assert
        Dictionary<string, string> seat3A = await _store.GetStoredSeatAsync(tripId, "3A");
        Dictionary<string, string> seat3B = await _store.GetStoredSeatAsync(tripId, "3B");
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        Assert.Equal("Available", seat3A["status"]);
        Assert.Equal("Available", seat3B["status"]);
        Assert.Equal("BookingFailed", seat3A["lastChangeReason"]);
        Assert.Equal("booking-correlation", seat3A["lastChangeCorrelationId"]);
        Assert.Equal(ReleaseFailedBookingSeatStore.Now.UtcDateTime.ToString("O"), seat3A["lastChangedAt"]);
        Assert.Equal(seat3A["lastChangeRequest"], seat3B["lastChangeRequest"]);
        Assert.False(seat3A.ContainsKey("bookingId"));
        Assert.False(seat3A.ContainsKey("holdExpiresAt"));
    }

    // AC-2
    [Fact]
    public async Task HandleAsync_BookingAlreadyBooked_LeavesTheSeatsBooked()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();
        await _store.PutTripAsync(tripId, false);
        await _store.PutOwnedSeatAsync(tripId, "3A", "Booked", bookingId);

        // Act
        EventOutcome outcome = await _store.Handler!.HandleAsync(ReleaseFailedBookingSeatStore.Event(bookingId, tripId), CancellationToken.None);

        // Assert
        Dictionary<string, string> seat3A = await _store.GetStoredSeatAsync(tripId, "3A");
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        Assert.Equal("Booked", seat3A["status"]);
        Assert.Equal(bookingId.ToString("D"), seat3A["bookingId"]);
        Assert.Equal("seed", seat3A["lastChangeRequest"]);
    }

    // AC-3
    [Fact]
    public async Task HandleAsync_DeliveredTwice_SecondChangesNothing()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();
        await _store.PutTripAsync(tripId, false);
        await _store.PutOwnedSeatAsync(tripId, "5A", "Held", bookingId);
        await _store.Handler!.HandleAsync(ReleaseFailedBookingSeatStore.Event(bookingId, tripId), CancellationToken.None);
        Dictionary<string, string> afterFirst = await _store.GetStoredSeatAsync(tripId, "5A");

        // Act
        EventOutcome outcome = await _store.Handler.HandleAsync(ReleaseFailedBookingSeatStore.Event(bookingId, tripId), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        Assert.Equal(afterFirst, await _store.GetStoredSeatAsync(tripId, "5A"));
    }

    // AC-3: a seat another booking holds now is never touched
    [Fact]
    public async Task HandleAsync_SeatHeldByAnotherBooking_LeavesTheOtherHoldAlone()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();
        var otherBookingId = Guid.NewGuid();
        await _store.PutTripAsync(tripId, false);
        await _store.PutOwnedSeatAsync(tripId, "6A", "Held", otherBookingId);

        // Act
        await _store.Handler!.HandleAsync(ReleaseFailedBookingSeatStore.Event(bookingId, tripId), CancellationToken.None);

        // Assert
        Dictionary<string, string> seat6A = await _store.GetStoredSeatAsync(tripId, "6A");
        Assert.Equal("Held", seat6A["status"]);
        Assert.Equal(otherBookingId.ToString("D"), seat6A["bookingId"]);
    }

    // Business rule 6: on a cancelled trip the freed seats are blocked
    [Fact]
    public async Task HandleAsync_CancelledTrip_BlocksTheSeatsInsteadOfFreeingThem()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();
        await _store.PutTripAsync(tripId, true);
        await _store.PutOwnedSeatAsync(tripId, "7A", "Held", bookingId);

        // Act
        await _store.Handler!.HandleAsync(ReleaseFailedBookingSeatStore.Event(bookingId, tripId), CancellationToken.None);

        // Assert
        Dictionary<string, string> seat7A = await _store.GetStoredSeatAsync(tripId, "7A");
        Assert.Equal("Blocked", seat7A["status"]);
        Assert.Equal("TripCancelled", seat7A["blockReason"]);
        Assert.Equal("BookingFailed", seat7A["lastChangeReason"]);
    }

    // Business rule 5: a trip not known (or already removed) is treated as nothing to free
    [Fact]
    public async Task HandleAsync_TripNotKnown_AppliedWithNothingToFree()
    {
        // Arrange
        ReceivedEvent bookingFailed = ReleaseFailedBookingSeatStore.Event(Guid.NewGuid(), Guid.NewGuid());

        // Act
        EventOutcome outcome = await _store.Handler!.HandleAsync(bookingFailed, CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
    }

    // AC-8
    [Theory]
    [InlineData("bookingId")]
    [InlineData("tripId")]
    public async Task HandleAsync_IdentifierMissing_RejectedAsInvalidEventData(string missingField)
    {
        // Arrange
        var data = new Dictionary<string, object?> { { "bookingId", Guid.NewGuid() }, { "tripId", Guid.NewGuid() } };
        data.Remove(missingField);

        // Act
        EventOutcome outcome = await _store.Handler!.HandleAsync(ReleaseFailedBookingSeatStore.EventWith(data), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Rejected, outcome.Kind);
        Assert.Equal("invalidEventData", outcome.Reason);
    }
}
