namespace Api.Features.Holds.ConfirmHold;

public sealed class ConfirmHoldTests : IClassFixture<ConfirmHoldSeatStore>
{
    private static readonly DateTimeOffset _departure = ConfirmHoldSeatStore.Now.AddHours(6);
    private static readonly DateTimeOffset _expiry = ConfirmHoldSeatStore.Now.AddMinutes(10);

    private readonly ConfirmHoldSeatStore _store;

    public ConfirmHoldTests(ConfirmHoldSeatStore store)
    {
        _store = store;
    }

    public static IEnumerable<object?[]> MissingOrWrongCredentials()
    {
        yield return new object?[] { null, null };
        yield return new object?[] { "not-the-internal-key", null };
        yield return new object?[] { null, ConfirmHoldSeatStore.AdminToken() };
    }

    [Fact]
    public async Task ConfirmHold_HeldSeats_BooksThemTogetherWithNoExpiry()
    {
        // Arrange
        Guid tripId = await PutTripAsync(_departure, false);
        var bookingId = Guid.NewGuid();
        await _store.PutHeldSeatAsync(tripId, "3A", bookingId, _expiry);
        await _store.PutHeldSeatAsync(tripId, "3B", bookingId, _expiry);
        HttpRequestMessage request = ConfirmHoldSeatStore.ConfirmRequestWith(
            tripId.ToString(), bookingId.ToString(), ConfirmHoldSeatStore.InternalKey, null);
        request.Headers.Add("X-Correlation-Id", "corr-confirm-ac13");

        // Act
        HttpResponseMessage response = await _store.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ConfirmHoldResponse hold = await ConfirmHoldSeatStore.ReadHoldAsync(response);
        Assert.Equal(HoldStatus.Booked, hold.Status);
        Assert.Equal(new[] { "3A", "3B" }, hold.SeatNos);
        Assert.Null(hold.HoldExpiresAt);
        Dictionary<string, string> seat3A = await _store.GetStoredSeatAsync(tripId, "3A");
        Dictionary<string, string> seat3B = await _store.GetStoredSeatAsync(tripId, "3B");
        Assert.Equal("Booked", seat3A["status"]);
        Assert.Equal(bookingId.ToString(), seat3A["bookingId"]);
        Assert.False(seat3A.ContainsKey("holdExpiresAt"));
        Assert.Equal("Booked", seat3A["lastChangeReason"]);
        Assert.Equal("corr-confirm-ac13", seat3A["lastChangeCorrelationId"]);
        Assert.Equal("Booked", seat3B["status"]);
        Assert.Equal(seat3A["lastChangeRequest"], seat3B["lastChangeRequest"]);
    }

    [Fact]
    public async Task ConfirmHold_ConfirmedThreeTimes_SameAnswerAndOneChange()
    {
        // Arrange
        Guid tripId = await PutTripAsync(_departure, false);
        var bookingId = Guid.NewGuid();
        await _store.PutHeldSeatAsync(tripId, "3A", bookingId, _expiry);
        await _store.PutHeldSeatAsync(tripId, "3B", bookingId, _expiry);
        await _store.ConfirmAsync(tripId, bookingId);
        Dictionary<string, string> afterFirst = await _store.GetStoredSeatAsync(tripId, "3A");

        // Act
        HttpResponseMessage second = await _store.ConfirmAsync(tripId, bookingId);
        HttpResponseMessage third = await _store.ConfirmAsync(tripId, bookingId);

        // Assert
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
        ConfirmHoldResponse hold = await ConfirmHoldSeatStore.ReadHoldAsync(third);
        Assert.Equal(HoldStatus.Booked, hold.Status);
        Assert.Equal(new[] { "3A", "3B" }, hold.SeatNos);
        Dictionary<string, string> afterThird = await _store.GetStoredSeatAsync(tripId, "3A");
        Assert.Equal(afterFirst["lastChangeRequest"], afterThird["lastChangeRequest"]);
    }

    [Fact]
    public async Task ConfirmHold_AfterTheExpiryBeforeTheTimerRan_BooksTheSeats()
    {
        // Arrange
        Guid tripId = await PutTripAsync(_departure, false);
        var bookingId = Guid.NewGuid();
        await _store.PutHeldSeatAsync(tripId, "3A", bookingId, ConfirmHoldSeatStore.Now.AddMinutes(-1));

        // Act
        HttpResponseMessage response = await _store.ConfirmAsync(tripId, bookingId);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Dictionary<string, string> seat = await _store.GetStoredSeatAsync(tripId, "3A");
        Assert.Equal("Booked", seat["status"]);
    }

    [Fact]
    public async Task ConfirmHold_HoldReleasedAndSeatNowHeldByAnother_RefusedHoldNotActiveAndSeatUntouched()
    {
        // Arrange
        Guid tripId = await PutTripAsync(_departure, false);
        var otherBooking = Guid.NewGuid();
        await _store.PutHeldSeatAsync(tripId, "3A", otherBooking, _expiry);

        // Act
        HttpResponseMessage response = await _store.ConfirmAsync(tripId, Guid.NewGuid());

        // Assert
        ConfirmHoldProblem problem = await ConfirmHoldSeatStore.ReadProblemAsync(response);
        Assert.Equal(HttpStatusCode.Conflict, problem.Status);
        Assert.Equal("HOLD_NOT_ACTIVE", problem.Code);
        Dictionary<string, string> seat = await _store.GetStoredSeatAsync(tripId, "3A");
        Assert.Equal("Held", seat["status"]);
        Assert.Equal(otherBooking.ToString(), seat["bookingId"]);
        Assert.Equal("seed", seat["lastChangeRequest"]);
    }

    [Fact]
    public async Task ConfirmHold_BookingNeverHeldAnything_RefusedHoldNotActive()
    {
        // Arrange
        Guid tripId = await PutTripAsync(_departure, false);
        await _store.PutAvailableSeatAsync(tripId, "3A");

        // Act
        HttpResponseMessage response = await _store.ConfirmAsync(tripId, Guid.NewGuid());

        // Assert
        ConfirmHoldProblem problem = await ConfirmHoldSeatStore.ReadProblemAsync(response);
        Assert.Equal(HttpStatusCode.Conflict, problem.Status);
        Assert.Equal("HOLD_NOT_ACTIVE", problem.Code);
    }

    [Fact]
    public async Task ConfirmHold_CancelledTrip_RefusedTripCancelledAndSeatStaysHeld()
    {
        // Arrange
        Guid tripId = await PutTripAsync(_departure, true);
        var bookingId = Guid.NewGuid();
        await _store.PutHeldSeatAsync(tripId, "3A", bookingId, _expiry);

        // Act
        HttpResponseMessage response = await _store.ConfirmAsync(tripId, bookingId);

        // Assert
        ConfirmHoldProblem problem = await ConfirmHoldSeatStore.ReadProblemAsync(response);
        Assert.Equal(HttpStatusCode.Conflict, problem.Status);
        Assert.Equal("TRIP_CANCELLED", problem.Code);
        Dictionary<string, string> seat = await _store.GetStoredSeatAsync(tripId, "3A");
        Assert.Equal("Held", seat["status"]);
        Assert.Equal(bookingId.ToString(), seat["bookingId"]);
    }

    [Fact]
    public async Task ConfirmHold_TripAlreadyDeparted_StillBooksTheSeats()
    {
        // Arrange
        Guid tripId = await PutTripAsync(ConfirmHoldSeatStore.Now.AddMinutes(-3), false);
        var bookingId = Guid.NewGuid();
        await _store.PutHeldSeatAsync(tripId, "3A", bookingId, _expiry);

        // Act
        HttpResponseMessage response = await _store.ConfirmAsync(tripId, bookingId);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Dictionary<string, string> seat = await _store.GetStoredSeatAsync(tripId, "3A");
        Assert.Equal("Booked", seat["status"]);
    }

    [Fact]
    public async Task ConfirmHold_UnknownTrip_RefusedNotFound()
    {
        // Act
        HttpResponseMessage response = await _store.ConfirmAsync(Guid.NewGuid(), Guid.NewGuid());

        // Assert
        ConfirmHoldProblem problem = await ConfirmHoldSeatStore.ReadProblemAsync(response);
        Assert.Equal(HttpStatusCode.NotFound, problem.Status);
        Assert.Equal("NOT_FOUND", problem.Code);
    }

    [Theory]
    [InlineData("not-a-trip", "3f1c2e4a-5b6d-4e7f-8a9b-0c1d2e3f4a5b", "tripId")]
    [InlineData("3f1c2e4a-5b6d-4e7f-8a9b-0c1d2e3f4a5b", "B-77", "bookingId")]
    public async Task ConfirmHold_MalformedIdentifier_RefusedValidationFailedNamingIt(string tripId, string bookingId, string field)
    {
        // Arrange
        HttpRequestMessage request = ConfirmHoldSeatStore.ConfirmRequestWith(tripId, bookingId, ConfirmHoldSeatStore.InternalKey, null);

        // Act
        HttpResponseMessage response = await _store.SendAsync(request);

        // Assert
        ConfirmHoldProblem problem = await ConfirmHoldSeatStore.ReadProblemAsync(response);
        Assert.Equal(HttpStatusCode.BadRequest, problem.Status);
        Assert.Equal("VALIDATION_FAILED", problem.Code);
        Assert.Contains(field, problem.ErrorFields);
    }

    [Theory]
    [MemberData(nameof(MissingOrWrongCredentials))]
    public async Task ConfirmHold_NoKeyWrongKeyOrOnlyAnAccessToken_RefusedUnauthorizedAndNothingChanges(
        string? internalKey, string? accessToken)
    {
        // Arrange
        Guid tripId = await PutTripAsync(_departure, false);
        var bookingId = Guid.NewGuid();
        await _store.PutHeldSeatAsync(tripId, "3A", bookingId, _expiry);
        HttpRequestMessage request = ConfirmHoldSeatStore.ConfirmRequestWith(
            tripId.ToString(), bookingId.ToString(), internalKey, accessToken);

        // Act
        HttpResponseMessage response = await _store.SendAsync(request);

        // Assert
        ConfirmHoldProblem problem = await ConfirmHoldSeatStore.ReadProblemAsync(response);
        Assert.Equal(HttpStatusCode.Unauthorized, problem.Status);
        Assert.Equal("UNAUTHORIZED", problem.Code);
        Dictionary<string, string> seat = await _store.GetStoredSeatAsync(tripId, "3A");
        Assert.Equal("Held", seat["status"]);
    }

    private async Task<Guid> PutTripAsync(DateTimeOffset departureAt, bool isCancelled)
    {
        var tripId = Guid.NewGuid();
        await _store.PutTripAsync(tripId, departureAt, isCancelled);
        return tripId;
    }
}
