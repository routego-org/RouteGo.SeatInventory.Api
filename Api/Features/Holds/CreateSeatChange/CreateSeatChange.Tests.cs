namespace Api.Features.Holds.CreateSeatChange;

public sealed class CreateSeatChangeTests : IClassFixture<CreateSeatChangeSeatStore>
{
    private static readonly DateTimeOffset _departure = CreateSeatChangeSeatStore.Now.AddHours(6);

    private readonly CreateSeatChangeSeatStore _store;

    public CreateSeatChangeTests(CreateSeatChangeSeatStore store)
    {
        _store = store;
    }

    [Fact]
    public async Task CreateSeatChange_FreeSeatOfSameClass_MovesTheBookingAndRecordsTheMove()
    {
        // Arrange
        (Guid tripId, Guid bookingId) = await PutBookingAsync("4C");
        await _store.PutAvailableSeatAsync(tripId, "5C", "Seater");
        HttpRequestMessage request = CreateSeatChangeSeatStore.ChangeRequestWith(
            tripId.ToString(), bookingId.ToString(), CreateSeatChangeSeatStore.InternalKey, CreateSeatChangeSeatStore.Moves("4C>5C"));
        request.Headers.Add("X-Correlation-Id", "corr-change-ac1");

        // Act
        HttpResponseMessage response = await _store.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        CreateSeatChangeResponse change = await CreateSeatChangeSeatStore.ReadChangeAsync(response);
        Assert.Equal(new[] { "5C" }, change.SeatNos);
        Assert.Equal(new[] { new SeatMove("4C", "5C") }, change.Changes);
        Dictionary<string, string> seat4C = await _store.GetStoredSeatAsync(tripId, "4C");
        Dictionary<string, string> seat5C = await _store.GetStoredSeatAsync(tripId, "5C");
        Assert.Equal("Available", seat4C["status"]);
        Assert.False(seat4C.ContainsKey("bookingId"));
        Assert.Equal("SeatChanged", seat4C["lastChangeReason"]);
        Assert.Equal("Booked", seat5C["status"]);
        Assert.Equal(bookingId.ToString(), seat5C["bookingId"]);
        Assert.Equal("SeatChanged", seat5C["lastChangeReason"]);
        Assert.Equal("4C", seat5C["lastChangeMovedFrom"]);
        Assert.Equal("corr-change-ac1", seat5C["lastChangeCorrelationId"]);
        Assert.Equal(seat4C["lastChangeRequest"], seat5C["lastChangeRequest"]);
    }

    [Fact]
    public async Task CreateSeatChange_OneNewSeatTaken_RefusedSeatsUnavailableAndNothingMoves()
    {
        // Arrange
        (Guid tripId, Guid bookingId) = await PutBookingAsync("4C", "4D");
        await _store.PutAvailableSeatAsync(tripId, "5C", "Seater");
        await _store.PutSeatOfAsync(tripId, "5D", "Held", Guid.NewGuid());

        // Act
        HttpResponseMessage response = await _store.ChangeAsync(tripId, bookingId, "4C>5C", "4D>5D");

        // Assert
        CreateSeatChangeProblem problem = await CreateSeatChangeSeatStore.ReadProblemAsync(response);
        Assert.Equal(HttpStatusCode.Conflict, problem.Status);
        Assert.Equal("SEATS_UNAVAILABLE", problem.Code);
        Assert.Equal(new[] { "5D" }, problem.NamedSeats);
        Dictionary<string, string> seat4C = await _store.GetStoredSeatAsync(tripId, "4C");
        Dictionary<string, string> seat5C = await _store.GetStoredSeatAsync(tripId, "5C");
        Assert.Equal("Booked", seat4C["status"]);
        Assert.Equal(bookingId.ToString(), seat4C["bookingId"]);
        Assert.Equal("Available", seat5C["status"]);
    }

    [Fact]
    public async Task CreateSeatChange_DifferentClass_RefusedSeatClassMismatchNamingTheMove()
    {
        // Arrange
        (Guid tripId, Guid bookingId) = await PutBookingAsync("4C");
        await _store.PutAvailableSeatAsync(tripId, "L3", "Sleeper");

        // Act
        HttpResponseMessage response = await _store.ChangeAsync(tripId, bookingId, "4C>L3");

        // Assert
        CreateSeatChangeProblem problem = await CreateSeatChangeSeatStore.ReadProblemAsync(response);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, problem.Status);
        Assert.Equal("SEAT_CLASS_MISMATCH", problem.Code);
        Assert.Equal(new[] { "4C>L3" }, problem.NamedMoves);
        Dictionary<string, string> seatL3 = await _store.GetStoredSeatAsync(tripId, "L3");
        Assert.Equal("Available", seatL3["status"]);
    }

    [Fact]
    public async Task CreateSeatChange_SeatBookedByAnotherBooking_RefusedSeatsNotOwned()
    {
        // Arrange
        Guid tripId = await PutTripAsync(false);
        await _store.PutSeatOfAsync(tripId, "4C", "Booked", Guid.NewGuid());
        await _store.PutAvailableSeatAsync(tripId, "5C", "Seater");

        // Act
        HttpResponseMessage response = await _store.ChangeAsync(tripId, Guid.NewGuid(), "4C>5C");

        // Assert
        CreateSeatChangeProblem problem = await CreateSeatChangeSeatStore.ReadProblemAsync(response);
        Assert.Equal(HttpStatusCode.Conflict, problem.Status);
        Assert.Equal("SEATS_NOT_OWNED", problem.Code);
        Assert.Equal(new[] { "4C" }, problem.NamedSeats);
    }

    [Fact]
    public async Task CreateSeatChange_SeatOnlyHeldByTheBooking_RefusedSeatsNotOwnedAndNothingChanges()
    {
        // Arrange
        Guid tripId = await PutTripAsync(false);
        var bookingId = Guid.NewGuid();
        await _store.PutSeatOfAsync(tripId, "3A", "Held", bookingId);
        await _store.PutAvailableSeatAsync(tripId, "5C", "Seater");

        // Act
        HttpResponseMessage response = await _store.ChangeAsync(tripId, bookingId, "3A>5C");

        // Assert
        CreateSeatChangeProblem problem = await CreateSeatChangeSeatStore.ReadProblemAsync(response);
        Assert.Equal("SEATS_NOT_OWNED", problem.Code);
        Assert.Equal(new[] { "3A" }, problem.NamedSeats);
        Dictionary<string, string> seat3A = await _store.GetStoredSeatAsync(tripId, "3A");
        Assert.Equal("Held", seat3A["status"]);
    }

    [Fact]
    public async Task CreateSeatChange_SameChangeAgain_SucceedsOkAndChangesNothing()
    {
        // Arrange
        (Guid tripId, Guid bookingId) = await PutBookingAsync("4C");
        await _store.PutAvailableSeatAsync(tripId, "5C", "Seater");
        await _store.ChangeAsync(tripId, bookingId, "4C>5C");
        Dictionary<string, string> afterFirst = await _store.GetStoredSeatAsync(tripId, "5C");

        // Act
        HttpResponseMessage response = await _store.ChangeAsync(tripId, bookingId, "4C>5C");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        CreateSeatChangeResponse change = await CreateSeatChangeSeatStore.ReadChangeAsync(response);
        Assert.Equal(new[] { "5C" }, change.SeatNos);
        Dictionary<string, string> afterSecond = await _store.GetStoredSeatAsync(tripId, "5C");
        Assert.Equal(afterFirst["lastChangeRequest"], afterSecond["lastChangeRequest"]);
    }

    [Fact]
    public async Task CreateSeatChange_RepeatWithOneMoreMove_MakesOnlyTheNewMove()
    {
        // Arrange
        (Guid tripId, Guid bookingId) = await PutBookingAsync("4C", "4D");
        await _store.PutAvailableSeatAsync(tripId, "5C", "Seater");
        await _store.PutAvailableSeatAsync(tripId, "5D", "Seater");
        await _store.ChangeAsync(tripId, bookingId, "4C>5C");
        Dictionary<string, string> afterFirst = await _store.GetStoredSeatAsync(tripId, "5C");

        // Act
        HttpResponseMessage response = await _store.ChangeAsync(tripId, bookingId, "4C>5C", "4D>5D");

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        CreateSeatChangeResponse change = await CreateSeatChangeSeatStore.ReadChangeAsync(response);
        Assert.Equal(new[] { "5C", "5D" }, change.SeatNos);
        Dictionary<string, string> seat5C = await _store.GetStoredSeatAsync(tripId, "5C");
        Dictionary<string, string> seat5D = await _store.GetStoredSeatAsync(tripId, "5D");
        Assert.Equal(afterFirst["lastChangeRequest"], seat5C["lastChangeRequest"]);
        Assert.Equal("4D", seat5D["lastChangeMovedFrom"]);
    }

    [Fact]
    public async Task CreateSeatChange_ToASeatTheBookingAlsoOwns_RefusedSeatsUnavailable()
    {
        // Arrange
        (Guid tripId, Guid bookingId) = await PutBookingAsync("4C", "4D");

        // Act
        HttpResponseMessage response = await _store.ChangeAsync(tripId, bookingId, "4C>4D");

        // Assert
        CreateSeatChangeProblem problem = await CreateSeatChangeSeatStore.ReadProblemAsync(response);
        Assert.Equal(HttpStatusCode.Conflict, problem.Status);
        Assert.Equal("SEATS_UNAVAILABLE", problem.Code);
        Assert.Equal(new[] { "4D" }, problem.NamedSeats);
    }

    [Fact]
    public async Task CreateSeatChange_PartOfTheBooking_MovesOnlyThoseSeats()
    {
        // Arrange
        (Guid tripId, Guid bookingId) = await PutBookingAsync("4C", "4D");
        await _store.PutAvailableSeatAsync(tripId, "5C", "Seater");

        // Act
        HttpResponseMessage response = await _store.ChangeAsync(tripId, bookingId, "4C>5C");

        // Assert
        CreateSeatChangeResponse change = await CreateSeatChangeSeatStore.ReadChangeAsync(response);
        Assert.Equal(new[] { "4D", "5C" }, change.SeatNos);
        Dictionary<string, string> seat4D = await _store.GetStoredSeatAsync(tripId, "4D");
        Assert.Equal("Booked", seat4D["status"]);
        Assert.Equal("seed", seat4D["lastChangeRequest"]);
    }

    [Fact]
    public async Task CreateSeatChange_CancelledTrip_RefusedTripCancelled()
    {
        // Arrange
        Guid tripId = await PutTripAsync(true);
        var bookingId = Guid.NewGuid();
        await _store.PutSeatOfAsync(tripId, "4C", "Booked", bookingId);
        await _store.PutAvailableSeatAsync(tripId, "5C", "Seater");

        // Act
        HttpResponseMessage response = await _store.ChangeAsync(tripId, bookingId, "4C>5C");

        // Assert
        CreateSeatChangeProblem problem = await CreateSeatChangeSeatStore.ReadProblemAsync(response);
        Assert.Equal(HttpStatusCode.Conflict, problem.Status);
        Assert.Equal("TRIP_CANCELLED", problem.Code);
    }

    [Fact]
    public async Task CreateSeatChange_DepartedTrip_RefusedTripDeparted()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        await _store.PutTripAsync(tripId, CreateSeatChangeSeatStore.Now.AddMinutes(-1), false);
        var bookingId = Guid.NewGuid();
        await _store.PutSeatOfAsync(tripId, "4C", "Booked", bookingId);
        await _store.PutAvailableSeatAsync(tripId, "5C", "Seater");

        // Act
        HttpResponseMessage response = await _store.ChangeAsync(tripId, bookingId, "4C>5C");

        // Assert
        CreateSeatChangeProblem problem = await CreateSeatChangeSeatStore.ReadProblemAsync(response);
        Assert.Equal(HttpStatusCode.Conflict, problem.Status);
        Assert.Equal("TRIP_DEPARTED", problem.Code);
    }

    [Fact]
    public async Task CreateSeatChange_UnknownSeat_RefusedUnknownSeatsNamingIt()
    {
        // Arrange
        (Guid tripId, Guid bookingId) = await PutBookingAsync("4C");

        // Act
        HttpResponseMessage response = await _store.ChangeAsync(tripId, bookingId, "4C>Z99");

        // Assert
        CreateSeatChangeProblem problem = await CreateSeatChangeSeatStore.ReadProblemAsync(response);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, problem.Status);
        Assert.Equal("UNKNOWN_SEATS", problem.Code);
        Assert.Equal(new[] { "Z99" }, problem.NamedSeats);
    }

    private async Task<Guid> PutTripAsync(bool isCancelled)
    {
        var tripId = Guid.NewGuid();
        await _store.PutTripAsync(tripId, _departure, isCancelled);
        return tripId;
    }

    private async Task<TripBooking> PutBookingAsync(params string[] bookedSeatNos)
    {
        Guid tripId = await PutTripAsync(false);
        var bookingId = Guid.NewGuid();
        foreach (string seatNo in bookedSeatNos)
        {
            await _store.PutSeatOfAsync(tripId, seatNo, "Booked", bookingId);
        }

        return new TripBooking(tripId, bookingId);
    }

    /// <summary>A new open trip and a booking that owns some of its seats.</summary>
    /// <param name="TripId">The trip.</param>
    /// <param name="BookingId">The booking.</param>
    private sealed record TripBooking(Guid TripId, Guid BookingId);
}
