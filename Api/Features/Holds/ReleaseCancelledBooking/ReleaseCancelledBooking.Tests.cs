namespace Api.Features.Holds.ReleaseCancelledBooking;

/// <summary>
/// CAP-HOLD-008: Booking's BookingCancelled frees every seat the booking holds or owns on the trip — the
/// only way a Booked seat is freed. Seats are read back raw, because the change record on each freed seat
/// is what the seat-change stream turns into SeatReleased with reason BookingCancelled.
/// </summary>
public sealed class ReleaseCancelledBookingTests : IClassFixture<ReleaseCancelledBookingSeatStore>
{
    private readonly ReleaseCancelledBookingSeatStore _store;

    public ReleaseCancelledBookingTests(ReleaseCancelledBookingSeatStore store)
    {
        _store = store;
    }

    // AC-4
    [Fact]
    public async Task HandleAsync_CancelledBookingOwnsSeats_FreesThemTogetherWithReasonBookingCancelled()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();
        await _store.PutTripAsync(tripId, false);
        await _store.PutOwnedSeatAsync(tripId, "4C", "Booked", bookingId);
        await _store.PutOwnedSeatAsync(tripId, "4D", "Booked", bookingId);

        // Act
        EventOutcome outcome = await _store.Handler!.HandleAsync(
            ReleaseCancelledBookingSeatStore.Event(bookingId, tripId, "4C", "4D"), CancellationToken.None);

        // Assert
        Dictionary<string, string> seat4C = await _store.GetStoredSeatAsync(tripId, "4C");
        Dictionary<string, string> seat4D = await _store.GetStoredSeatAsync(tripId, "4D");
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        Assert.Equal("Available", seat4C["status"]);
        Assert.Equal("Available", seat4D["status"]);
        Assert.Equal("BookingCancelled", seat4C["lastChangeReason"]);
        Assert.Equal("booking-correlation", seat4C["lastChangeCorrelationId"]);
        Assert.Equal(ReleaseCancelledBookingSeatStore.Now.UtcDateTime.ToString("O"), seat4C["lastChangedAt"]);
        Assert.Equal(seat4C["lastChangeRequest"], seat4D["lastChangeRequest"]);
        Assert.False(seat4C.ContainsKey("bookingId"));
    }

    // Business rule 3: a seat the cancelled booking still holds is freed too
    [Fact]
    public async Task HandleAsync_CancelledBookingStillHoldsASeat_FreesItAsWell()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();
        await _store.PutTripAsync(tripId, false);
        await _store.PutOwnedSeatAsync(tripId, "2A", "Held", bookingId);

        // Act
        await _store.Handler!.HandleAsync(ReleaseCancelledBookingSeatStore.Event(bookingId, tripId, "2A"), CancellationToken.None);

        // Assert
        Dictionary<string, string> seat2A = await _store.GetStoredSeatAsync(tripId, "2A");
        Assert.Equal("Available", seat2A["status"]);
        Assert.False(seat2A.ContainsKey("holdExpiresAt"));
    }

    // AC-5
    [Fact]
    public async Task HandleAsync_DeliveredAgainAfterTheSeatWasHeldByAnotherBooking_LeavesTheNewHoldAlone()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();
        var nextBookingId = Guid.NewGuid();
        await _store.PutTripAsync(tripId, false);
        await _store.PutOwnedSeatAsync(tripId, "4C", "Booked", bookingId);
        ReceivedEvent bookingCancelled = ReleaseCancelledBookingSeatStore.Event(bookingId, tripId, "4C");
        await _store.Handler!.HandleAsync(bookingCancelled, CancellationToken.None);
        await _store.PutOwnedSeatAsync(tripId, "4C", "Held", nextBookingId);

        // Act
        EventOutcome outcome = await _store.Handler.HandleAsync(bookingCancelled, CancellationToken.None);

        // Assert
        Dictionary<string, string> seat4C = await _store.GetStoredSeatAsync(tripId, "4C");
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        Assert.Equal("Held", seat4C["status"]);
        Assert.Equal(nextBookingId.ToString("D"), seat4C["bookingId"]);
        Assert.Equal("seed", seat4C["lastChangeRequest"]);
    }

    // AC-6
    [Fact]
    public async Task HandleAsync_CancelledTrip_BlocksTheSeatsInsteadOfFreeingThem()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();
        await _store.PutTripAsync(tripId, true);
        await _store.PutOwnedSeatAsync(tripId, "4C", "Booked", bookingId);

        // Act
        await _store.Handler!.HandleAsync(ReleaseCancelledBookingSeatStore.Event(bookingId, tripId, "4C"), CancellationToken.None);

        // Assert
        Dictionary<string, string> seat4C = await _store.GetStoredSeatAsync(tripId, "4C");
        Assert.Equal("Blocked", seat4C["status"]);
        Assert.Equal("TripCancelled", seat4C["blockReason"]);
        Assert.Equal("BookingCancelled", seat4C["lastChangeReason"]);
    }

    // AC-7
    [Fact]
    public async Task HandleAsync_EventListsAnotherBookingsSeat_FreesOnlyTheBookingsOwnSeat()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();
        var otherBookingId = Guid.NewGuid();
        await _store.PutTripAsync(tripId, false);
        await _store.PutOwnedSeatAsync(tripId, "4C", "Booked", bookingId);
        await _store.PutOwnedSeatAsync(tripId, "4D", "Booked", otherBookingId);

        // Act
        await _store.Handler!.HandleAsync(ReleaseCancelledBookingSeatStore.Event(bookingId, tripId, "4C", "4D"), CancellationToken.None);

        // Assert
        Dictionary<string, string> seat4C = await _store.GetStoredSeatAsync(tripId, "4C");
        Dictionary<string, string> seat4D = await _store.GetStoredSeatAsync(tripId, "4D");
        Assert.Equal("Available", seat4C["status"]);
        Assert.Equal("Booked", seat4D["status"]);
        Assert.Equal(otherBookingId.ToString("D"), seat4D["bookingId"]);
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
        EventOutcome outcome = await _store.Handler!.HandleAsync(ReleaseCancelledBookingSeatStore.EventWith(data), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Rejected, outcome.Kind);
        Assert.Equal("invalidEventData", outcome.Reason);
    }
}
