namespace Api.Features.TripSeats.DeleteSeatBlock;

public sealed class DeleteSeatBlockTests : IClassFixture<DeleteSeatBlockSeatStore>
{
    private static readonly Guid _operatorOne = Guid.Parse("0b6f5c9e-1f0a-4a7e-9a52-3c1d2e4f5a61");
    private static readonly Guid _operatorTwo = Guid.Parse("7d2e8a14-5b3c-4f6d-8e9a-1b2c3d4e5f60");
    private static readonly Guid _operatorUser = Guid.Parse("c3a1b2d4-0e5f-4a6b-8c7d-9e0f1a2b3c4d");

    private readonly DeleteSeatBlockSeatStore _store;

    public DeleteSeatBlockTests(DeleteSeatBlockSeatStore store)
    {
        _store = store;
    }

    public static IEnumerable<object?[]> MalformedSeatLists()
    {
        yield return new object?[] { null };
        yield return new object?[] { string.Empty };
        yield return new object?[] { "L1,L1" };
        yield return new object?[] { "L1,,L2" };
        yield return new object?[] { string.Join(',', Enumerable.Range(1, 61).Select(i => $"S{i}")) };
    }

    [Fact]
    public async Task DeleteSeatBlock_OperatorBlockedSeats_MakesThemAvailableAndRecordsTheChange()
    {
        // Arrange
        Guid tripId = await PutOpenTripAsync();
        await _store.PutSeatAsync(tripId, "L1", "Blocked", null, "Operator");
        await _store.PutSeatAsync(tripId, "L2", "Blocked", null, "Operator");
        await _store.PutSeatAsync(tripId, "L3", "Blocked", null, "Operator");

        // Act
        HttpResponseMessage response = await _store.UnblockAsync(tripId.ToString(), OperatorOneToken(), "L2,L3");

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Dictionary<string, string> l1 = await _store.GetStoredSeatAsync(tripId, "L1");
        Dictionary<string, string> l2 = await _store.GetStoredSeatAsync(tripId, "L2");
        Dictionary<string, string> l3 = await _store.GetStoredSeatAsync(tripId, "L3");
        Assert.Equal("Blocked", l1["status"]);
        Assert.Equal("Available", l2["status"]);
        Assert.False(l2.ContainsKey("blockReason"));
        Assert.Equal("OperatorUnblocked", l2["lastChangeReason"]);
        Assert.Equal(_operatorUser.ToString(), l2["lastChangedBy"]);
        Assert.Equal(DeleteSeatBlockSeatStore.Now.UtcDateTime.ToString("O"), l2["lastChangedAt"]);
        Assert.True(l2.ContainsKey("lastChangeCorrelationId"));
        Assert.Equal("Available", l3["status"]);
        Assert.Equal(l2["lastChangeRequest"], l3["lastChangeRequest"]);
        Assert.Equal("available", await _store.GetSeatMapStatusAsync(tripId, "L2"));
    }

    [Fact]
    public async Task DeleteSeatBlock_SeatAlreadyAvailable_SucceedsWithNoChange()
    {
        // Arrange
        Guid tripId = await PutOpenTripAsync();
        await _store.PutSeatAsync(tripId, "L2", "Blocked", null, "Operator");
        await _store.UnblockAsync(tripId.ToString(), OperatorOneToken(), "L2");
        Dictionary<string, string> afterFirst = await _store.GetStoredSeatAsync(tripId, "L2");

        // Act
        HttpResponseMessage response = await _store.UnblockAsync(tripId.ToString(), OperatorOneToken(), "L2");

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Dictionary<string, string> afterRepeat = await _store.GetStoredSeatAsync(tripId, "L2");
        Assert.Equal(afterFirst["lastChangeRequest"], afterRepeat["lastChangeRequest"]);
    }

    [Fact]
    public async Task DeleteSeatBlock_BookedSeat_RefusedSeatsNotOperatorBlockedAndNothingChanges()
    {
        // Arrange
        Guid tripId = await PutOpenTripAsync();
        await _store.PutSeatAsync(tripId, "L1", "Blocked", null, "Operator");
        await _store.PutSeatAsync(tripId, "4C", "Booked", "B-55", null);

        // Act
        DeleteSeatBlockProblem problem = await _store.UnblockRefusedAsync(tripId.ToString(), OperatorOneToken(), "L1,4C");

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, problem.Status);
        Assert.Equal("SEATS_NOT_OPERATOR_BLOCKED", problem.Code);
        Assert.Equal(new[] { "4C" }, problem.NamedSeats);
        Dictionary<string, string> l1 = await _store.GetStoredSeatAsync(tripId, "L1");
        Dictionary<string, string> seat4C = await _store.GetStoredSeatAsync(tripId, "4C");
        Assert.Equal("Blocked", l1["status"]);
        Assert.Equal("Booked", seat4C["status"]);
        Assert.Equal("B-55", seat4C["bookingId"]);
    }

    [Fact]
    public async Task DeleteSeatBlock_SeatBlockedByCancellation_RefusedSeatsNotOperatorBlocked()
    {
        // Arrange
        Guid tripId = await PutOpenTripAsync();
        await _store.PutSeatAsync(tripId, "L1", "Blocked", null, "TripCancelled");

        // Act
        DeleteSeatBlockProblem problem = await _store.UnblockRefusedAsync(tripId.ToString(), OperatorOneToken(), "L1");

        // Assert
        Assert.Equal("SEATS_NOT_OPERATOR_BLOCKED", problem.Code);
        Assert.Equal(new[] { "L1" }, problem.NamedSeats);
        Dictionary<string, string> l1 = await _store.GetStoredSeatAsync(tripId, "L1");
        Assert.Equal("Blocked", l1["status"]);
    }

    [Fact]
    public async Task DeleteSeatBlock_CancelledTrip_RefusedTripCancelledAndSeatsStayBlocked()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        await _store.PutTripAsync(tripId, _operatorOne, DeleteSeatBlockSeatStore.Departure, true);
        await _store.PutSeatAsync(tripId, "L1", "Blocked", null, "Operator");
        await _store.PutSeatAsync(tripId, "L2", "Blocked", null, "TripCancelled");

        // Act
        DeleteSeatBlockProblem problem = await _store.UnblockRefusedAsync(tripId.ToString(), OperatorOneToken(), "L1,L2");

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, problem.Status);
        Assert.Equal("TRIP_CANCELLED", problem.Code);
        Dictionary<string, string> l1 = await _store.GetStoredSeatAsync(tripId, "L1");
        Dictionary<string, string> l2 = await _store.GetStoredSeatAsync(tripId, "L2");
        Assert.Equal("Blocked", l1["status"]);
        Assert.Equal("Blocked", l2["status"]);
    }

    [Fact]
    public async Task DeleteSeatBlock_DepartedTrip_RefusedTripDepartedWithDepartureTime()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        DateTimeOffset departedAt = DeleteSeatBlockSeatStore.Now.AddHours(-1);
        await _store.PutTripAsync(tripId, _operatorOne, departedAt, false);
        await _store.PutSeatAsync(tripId, "L1", "Blocked", null, "Operator");

        // Act
        DeleteSeatBlockProblem problem = await _store.UnblockRefusedAsync(tripId.ToString(), OperatorOneToken(), "L1");

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, problem.Status);
        Assert.Equal("TRIP_DEPARTED", problem.Code);
        Assert.Equal(departedAt, DateTimeOffset.Parse(problem.DepartedAt!, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task DeleteSeatBlock_SeatTheTripDoesNotHave_RefusedUnknownSeatsAndNothingChanges()
    {
        // Arrange
        Guid tripId = await PutOpenTripAsync();
        await _store.PutSeatAsync(tripId, "L1", "Blocked", null, "Operator");

        // Act
        DeleteSeatBlockProblem problem = await _store.UnblockRefusedAsync(tripId.ToString(), OperatorOneToken(), "L1,Z99");

        // Assert
        Assert.Equal(HttpStatusCode.UnprocessableEntity, problem.Status);
        Assert.Equal("UNKNOWN_SEATS", problem.Code);
        Assert.Equal(new[] { "Z99" }, problem.NamedSeats);
        Dictionary<string, string> l1 = await _store.GetStoredSeatAsync(tripId, "L1");
        Assert.Equal("Blocked", l1["status"]);
    }

    [Theory]
    [MemberData(nameof(MalformedSeatLists))]
    public async Task DeleteSeatBlock_MissingEmptyDuplicateBlankOrOverSixtySeats_RefusedValidationFailedOnSeatNos(string? seatNos)
    {
        // Arrange
        Guid tripId = await PutOpenTripAsync();

        // Act
        DeleteSeatBlockProblem problem = await _store.UnblockRefusedAsync(tripId.ToString(), OperatorOneToken(), seatNos);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, problem.Status);
        Assert.Equal("VALIDATION_FAILED", problem.Code);
        Assert.Contains("seatNos", problem.ErrorFields);
    }

    [Fact]
    public async Task DeleteSeatBlock_MalformedTripId_RefusedValidationFailedOnTripId()
    {
        // Act
        DeleteSeatBlockProblem problem = await _store.UnblockRefusedAsync("not-a-trip", OperatorOneToken(), "L1");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, problem.Status);
        Assert.Contains("tripId", problem.ErrorFields);
    }

    [Fact]
    public async Task DeleteSeatBlock_AnotherOperatorsTrip_RefusedForbiddenAndNothingChanges()
    {
        // Arrange
        Guid tripId = await PutOpenTripAsync();
        await _store.PutSeatAsync(tripId, "L1", "Blocked", null, "Operator");
        string otherOperator = DeleteSeatBlockTokens.OperatorToken(_operatorTwo, Guid.NewGuid());

        // Act
        DeleteSeatBlockProblem problem = await _store.UnblockRefusedAsync(tripId.ToString(), otherOperator, "L1");

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, problem.Status);
        Assert.Equal("FORBIDDEN", problem.Code);
        Dictionary<string, string> l1 = await _store.GetStoredSeatAsync(tripId, "L1");
        Assert.Equal("Blocked", l1["status"]);
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Customer")]
    public async Task DeleteSeatBlock_AdminOrCustomer_RefusedForbidden(string role)
    {
        // Arrange
        Guid tripId = await PutOpenTripAsync();

        // Act
        DeleteSeatBlockProblem problem =
            await _store.UnblockRefusedAsync(tripId.ToString(), DeleteSeatBlockTokens.TokenFor(role), "L1");

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, problem.Status);
        Assert.Equal("FORBIDDEN", problem.Code);
    }

    [Fact]
    public async Task DeleteSeatBlock_NoAccessToken_RefusedUnauthorized()
    {
        // Arrange
        Guid tripId = await PutOpenTripAsync();

        // Act
        DeleteSeatBlockProblem problem = await _store.UnblockRefusedAsync(tripId.ToString(), null, "L1");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, problem.Status);
        Assert.Equal("UNAUTHORIZED", problem.Code);
    }

    [Fact]
    public async Task DeleteSeatBlock_UnknownTrip_RefusedNotFound()
    {
        // Act
        DeleteSeatBlockProblem problem = await _store.UnblockRefusedAsync(Guid.NewGuid().ToString(), OperatorOneToken(), "L1");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, problem.Status);
        Assert.Equal("NOT_FOUND", problem.Code);
    }

    private static string OperatorOneToken()
    {
        return DeleteSeatBlockTokens.OperatorToken(_operatorOne, _operatorUser);
    }

    private async Task<Guid> PutOpenTripAsync()
    {
        var tripId = Guid.NewGuid();
        await _store.PutTripAsync(tripId, _operatorOne, DeleteSeatBlockSeatStore.Departure, false);
        return tripId;
    }
}
