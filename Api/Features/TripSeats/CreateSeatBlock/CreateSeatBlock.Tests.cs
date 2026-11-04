namespace Api.Features.TripSeats.CreateSeatBlock;

public sealed class CreateSeatBlockTests : IClassFixture<CreateSeatBlockSeatStore>
{
    private static readonly Guid _operatorOne = Guid.Parse("0b6f5c9e-1f0a-4a7e-9a52-3c1d2e4f5a61");
    private static readonly Guid _operatorTwo = Guid.Parse("7d2e8a14-5b3c-4f6d-8e9a-1b2c3d4e5f60");
    private static readonly Guid _operatorUser = Guid.Parse("c3a1b2d4-0e5f-4a6b-8c7d-9e0f1a2b3c4d");

    private readonly CreateSeatBlockSeatStore _store;

    public CreateSeatBlockTests(CreateSeatBlockSeatStore store)
    {
        _store = store;
    }

    public static IEnumerable<object[]> MalformedSeatLists()
    {
        yield return new object[] { new List<string>() };
        yield return new object[] { new List<string> { "L1", "L1" } };
        yield return new object[] { Enumerable.Range(1, 61).Select(i => $"S{i}").ToList() };
    }

    [Fact]
    public async Task CreateSeatBlock_FreeSeats_BlocksThemAndRecordsTheChange()
    {
        // Arrange
        Guid tripId = await PutOpenTripAsync();
        await _store.PutSeatAsync(tripId, "L1", "Available", null, null);
        await _store.PutSeatAsync(tripId, "L2", "Available", null, null);

        // Act
        HttpResponseMessage response = await _store.BlockAsync(tripId.ToString(), OperatorOneToken(), SeatNos("L1", "L2"));

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        CreateSeatBlockResponse block = await _store.ReadBlockAsync(response);
        Assert.Equal(new[] { "L1", "L2" }, block.BlockedSeatNos);
        Dictionary<string, string> l1 = await _store.GetStoredSeatAsync(tripId, "L1");
        Dictionary<string, string> l2 = await _store.GetStoredSeatAsync(tripId, "L2");
        Assert.Equal("Blocked", l1["status"]);
        Assert.Equal("Operator", l1["blockReason"]);
        Assert.Equal("OperatorBlocked", l1["lastChangeReason"]);
        Assert.Equal(_operatorUser.ToString(), l1["lastChangedBy"]);
        Assert.True(Guid.TryParse(l1["lastChangeCorrelationId"], out _));
        Assert.Equal("Blocked", l2["status"]);
        Assert.Equal(l1["lastChangeRequest"], l2["lastChangeRequest"]);
        Assert.Equal(l1["lastChangeCorrelationId"], l2["lastChangeCorrelationId"]);
        Assert.Equal("blocked", await _store.GetSeatMapStatusAsync(tripId, "L1"));
    }

    [Fact]
    public async Task CreateSeatBlock_OneSeatHeld_RefusedSeatsUnavailableAndNothingChanges()
    {
        // Arrange
        Guid tripId = await PutOpenTripAsync();
        await _store.PutSeatAsync(tripId, "L1", "Available", null, null);
        await _store.PutSeatAsync(tripId, "L2", "Held", "B-77", null);

        // Act
        CreateSeatBlockProblem problem = await _store.BlockRefusedAsync(tripId.ToString(), OperatorOneToken(), SeatNos("L1", "L2"));

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, problem.Status);
        Assert.Equal("SEATS_UNAVAILABLE", problem.Code);
        Assert.Equal(new[] { "L2" }, problem.NamedSeats);
        Dictionary<string, string> l1 = await _store.GetStoredSeatAsync(tripId, "L1");
        Dictionary<string, string> l2 = await _store.GetStoredSeatAsync(tripId, "L2");
        Assert.Equal("Available", l1["status"]);
        Assert.Equal("seed", l1["lastChangeRequest"]);
        Assert.Equal("Held", l2["status"]);
        Assert.Equal("B-77", l2["bookingId"]);
    }

    [Fact]
    public async Task CreateSeatBlock_BookedSeat_RefusedSeatsUnavailableAndStaysBooked()
    {
        // Arrange
        Guid tripId = await PutOpenTripAsync();
        await _store.PutSeatAsync(tripId, "4C", "Booked", "B-55", null);

        // Act
        CreateSeatBlockProblem problem = await _store.BlockRefusedAsync(tripId.ToString(), OperatorOneToken(), SeatNos("4C"));

        // Assert
        Assert.Equal("SEATS_UNAVAILABLE", problem.Code);
        Assert.Equal(new[] { "4C" }, problem.NamedSeats);
        Dictionary<string, string> seat = await _store.GetStoredSeatAsync(tripId, "4C");
        Assert.Equal("Booked", seat["status"]);
        Assert.Equal("B-55", seat["bookingId"]);
    }

    [Fact]
    public async Task CreateSeatBlock_SeatBlockedByCancellation_RefusedSeatsUnavailable()
    {
        // Arrange
        Guid tripId = await PutOpenTripAsync();
        await _store.PutSeatAsync(tripId, "L1", "Blocked", null, "TripCancelled");

        // Act
        CreateSeatBlockProblem problem = await _store.BlockRefusedAsync(tripId.ToString(), OperatorOneToken(), SeatNos("L1"));

        // Assert
        Assert.Equal("SEATS_UNAVAILABLE", problem.Code);
        Assert.Equal(new[] { "L1" }, problem.NamedSeats);
    }

    [Fact]
    public async Task CreateSeatBlock_OneSeatAlreadyBlockedByOperator_BlocksOnlyTheOther()
    {
        // Arrange
        Guid tripId = await PutOpenTripAsync();
        await _store.PutSeatAsync(tripId, "L1", "Blocked", null, "Operator");
        await _store.PutSeatAsync(tripId, "L2", "Available", null, null);

        // Act
        HttpResponseMessage response = await _store.BlockAsync(tripId.ToString(), OperatorOneToken(), SeatNos("L1", "L2"));

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        CreateSeatBlockResponse block = await _store.ReadBlockAsync(response);
        Assert.Equal(new[] { "L1", "L2" }, block.SeatNos);
        Assert.Equal(new[] { "L2" }, block.BlockedSeatNos);
        Dictionary<string, string> l1 = await _store.GetStoredSeatAsync(tripId, "L1");
        Assert.Equal("seed", l1["lastChangeRequest"]);
    }

    [Fact]
    public async Task CreateSeatBlock_SameRequestRepeated_SucceedsWithNoChange()
    {
        // Arrange
        Guid tripId = await PutOpenTripAsync();
        await _store.PutSeatAsync(tripId, "L1", "Available", null, null);
        await _store.BlockAsync(tripId.ToString(), OperatorOneToken(), SeatNos("L1"));
        Dictionary<string, string> afterFirst = await _store.GetStoredSeatAsync(tripId, "L1");

        // Act
        HttpResponseMessage response = await _store.BlockAsync(tripId.ToString(), OperatorOneToken(), SeatNos("L1"));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        CreateSeatBlockResponse block = await _store.ReadBlockAsync(response);
        Assert.Empty(block.BlockedSeatNos);
        Dictionary<string, string> afterRepeat = await _store.GetStoredSeatAsync(tripId, "L1");
        Assert.Equal(afterFirst["lastChangeRequest"], afterRepeat["lastChangeRequest"]);
    }

    [Fact]
    public async Task CreateSeatBlock_CancelledTrip_RefusedTripCancelled()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        await _store.PutTripAsync(tripId, _operatorOne, CreateSeatBlockSeatStore.Departure, true);
        await _store.PutSeatAsync(tripId, "L1", "Blocked", null, "TripCancelled");

        // Act
        CreateSeatBlockProblem problem = await _store.BlockRefusedAsync(tripId.ToString(), OperatorOneToken(), SeatNos("L1"));

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, problem.Status);
        Assert.Equal("TRIP_CANCELLED", problem.Code);
    }

    [Fact]
    public async Task CreateSeatBlock_DepartedTrip_RefusedTripDepartedWithDepartureTime()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        DateTimeOffset departedAt = CreateSeatBlockSeatStore.Now.AddHours(-1);
        await _store.PutTripAsync(tripId, _operatorOne, departedAt, false);
        await _store.PutSeatAsync(tripId, "L1", "Available", null, null);

        // Act
        CreateSeatBlockProblem problem = await _store.BlockRefusedAsync(tripId.ToString(), OperatorOneToken(), SeatNos("L1"));

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, problem.Status);
        Assert.Equal("TRIP_DEPARTED", problem.Code);
        Assert.Equal(departedAt, DateTimeOffset.Parse(problem.DepartedAt!, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task CreateSeatBlock_SeatTheTripDoesNotHave_RefusedUnknownSeatsAndNothingChanges()
    {
        // Arrange
        Guid tripId = await PutOpenTripAsync();
        await _store.PutSeatAsync(tripId, "L1", "Available", null, null);

        // Act
        CreateSeatBlockProblem problem = await _store.BlockRefusedAsync(tripId.ToString(), OperatorOneToken(), SeatNos("L1", "Z99"));

        // Assert
        Assert.Equal(HttpStatusCode.UnprocessableEntity, problem.Status);
        Assert.Equal("UNKNOWN_SEATS", problem.Code);
        Assert.Equal(new[] { "Z99" }, problem.NamedSeats);
        Dictionary<string, string> l1 = await _store.GetStoredSeatAsync(tripId, "L1");
        Assert.Equal("Available", l1["status"]);
    }

    [Theory]
    [MemberData(nameof(MalformedSeatLists))]
    public async Task CreateSeatBlock_EmptyDuplicateOrOverSixtySeats_RefusedValidationFailedOnSeatNos(List<string> seatNos)
    {
        // Arrange
        Guid tripId = await PutOpenTripAsync();

        // Act
        CreateSeatBlockProblem problem = await _store.BlockRefusedAsync(tripId.ToString(), OperatorOneToken(), new { seatNos });

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, problem.Status);
        Assert.Equal("VALIDATION_FAILED", problem.Code);
        Assert.Contains("seatNos", problem.ErrorFields);
    }

    [Fact]
    public async Task CreateSeatBlock_AnotherOperatorsTrip_RefusedForbidden()
    {
        // Arrange
        Guid tripId = await PutOpenTripAsync();
        await _store.PutSeatAsync(tripId, "L1", "Available", null, null);
        string otherOperator = CreateSeatBlockSeatStore.OperatorToken(_operatorTwo, Guid.NewGuid());

        // Act
        CreateSeatBlockProblem problem = await _store.BlockRefusedAsync(tripId.ToString(), otherOperator, SeatNos("L1"));

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, problem.Status);
        Assert.Equal("FORBIDDEN", problem.Code);
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Customer")]
    public async Task CreateSeatBlock_AdminOrCustomer_RefusedForbidden(string role)
    {
        // Arrange
        Guid tripId = await PutOpenTripAsync();

        // Act
        CreateSeatBlockProblem problem =
            await _store.BlockRefusedAsync(tripId.ToString(), CreateSeatBlockSeatStore.TokenFor(role), SeatNos("L1"));

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, problem.Status);
        Assert.Equal("FORBIDDEN", problem.Code);
    }

    [Fact]
    public async Task CreateSeatBlock_NoAccessToken_RefusedUnauthorized()
    {
        // Arrange
        Guid tripId = await PutOpenTripAsync();

        // Act
        CreateSeatBlockProblem problem = await _store.BlockRefusedAsync(tripId.ToString(), null, SeatNos("L1"));

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, problem.Status);
        Assert.Equal("UNAUTHORIZED", problem.Code);
    }

    [Fact]
    public async Task CreateSeatBlock_UnknownTrip_RefusedNotFound()
    {
        // Act
        CreateSeatBlockProblem problem = await _store.BlockRefusedAsync(Guid.NewGuid().ToString(), OperatorOneToken(), SeatNos("L1"));

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, problem.Status);
        Assert.Equal("NOT_FOUND", problem.Code);
    }

    private static object SeatNos(params string[] seatNos)
    {
        return new { seatNos };
    }

    private static string OperatorOneToken()
    {
        return CreateSeatBlockSeatStore.OperatorToken(_operatorOne, _operatorUser);
    }

    private async Task<Guid> PutOpenTripAsync()
    {
        var tripId = Guid.NewGuid();
        await _store.PutTripAsync(tripId, _operatorOne, CreateSeatBlockSeatStore.Departure, false);
        return tripId;
    }
}
