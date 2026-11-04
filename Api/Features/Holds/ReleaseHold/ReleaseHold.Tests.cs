namespace Api.Features.Holds.ReleaseHold;

public sealed class ReleaseHoldTests : IClassFixture<ReleaseHoldSeatStore>
{
    private static readonly DateTimeOffset _departure = ReleaseHoldSeatStore.Now.AddHours(6);

    private readonly ReleaseHoldSeatStore _store;

    public ReleaseHoldTests(ReleaseHoldSeatStore store)
    {
        _store = store;
    }

    public static IEnumerable<object?[]> MissingOrWrongCredentials()
    {
        yield return new object?[] { null, null };
        yield return new object?[] { "not-the-internal-key", null };
        yield return new object?[] { null, ReleaseHoldSeatStore.AdminToken() };
    }

    [Fact]
    public async Task ReleaseHold_HeldSeats_MakesThemAvailableTogether()
    {
        // Arrange
        Guid tripId = await PutTripAsync(false);
        var bookingId = Guid.NewGuid();
        await _store.PutHeldSeatAsync(tripId, "3A", bookingId);
        await _store.PutHeldSeatAsync(tripId, "3B", bookingId);
        HttpRequestMessage request = ReleaseHoldSeatStore.ReleaseRequestWith(
            tripId.ToString(), bookingId.ToString(), ReleaseHoldSeatStore.InternalKey, null);
        request.Headers.Add("X-Correlation-Id", "corr-release-ac18");

        // Act
        HttpResponseMessage response = await _store.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ReleaseHoldResponse release = await ReleaseHoldSeatStore.ReadReleaseAsync(response);
        Assert.Equal(new[] { "3A", "3B" }, release.ReleasedSeatNos);
        Dictionary<string, string> seat3A = await _store.GetStoredSeatAsync(tripId, "3A");
        Dictionary<string, string> seat3B = await _store.GetStoredSeatAsync(tripId, "3B");
        Assert.Equal("Available", seat3A["status"]);
        Assert.False(seat3A.ContainsKey("bookingId"));
        Assert.False(seat3A.ContainsKey("holdExpiresAt"));
        Assert.Equal("HoldReleased", seat3A["lastChangeReason"]);
        Assert.Equal("corr-release-ac18", seat3A["lastChangeCorrelationId"]);
        Assert.Equal("Available", seat3B["status"]);
        Assert.Equal(seat3A["lastChangeRequest"], seat3B["lastChangeRequest"]);
    }

    [Fact]
    public async Task ReleaseHold_ReleasedTwice_SecondNamesNoSeatsAndChangesNothing()
    {
        // Arrange
        Guid tripId = await PutTripAsync(false);
        var bookingId = Guid.NewGuid();
        await _store.PutHeldSeatAsync(tripId, "3A", bookingId);
        await _store.ReleaseAsync(tripId, bookingId);
        Dictionary<string, string> afterFirst = await _store.GetStoredSeatAsync(tripId, "3A");

        // Act
        HttpResponseMessage response = await _store.ReleaseAsync(tripId, bookingId);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ReleaseHoldResponse release = await ReleaseHoldSeatStore.ReadReleaseAsync(response);
        Assert.Empty(release.ReleasedSeatNos);
        Dictionary<string, string> afterSecond = await _store.GetStoredSeatAsync(tripId, "3A");
        Assert.Equal(afterFirst["lastChangeRequest"], afterSecond["lastChangeRequest"]);
    }

    [Fact]
    public async Task ReleaseHold_BookingNeverHeldAnything_SucceedsNamingNoSeats()
    {
        // Arrange
        Guid tripId = await PutTripAsync(false);

        // Act
        HttpResponseMessage response = await _store.ReleaseAsync(tripId, Guid.NewGuid());

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ReleaseHoldResponse release = await ReleaseHoldSeatStore.ReadReleaseAsync(response);
        Assert.Empty(release.ReleasedSeatNos);
    }

    [Fact]
    public async Task ReleaseHold_AnotherBookingsSeat_IsLeftAlone()
    {
        // Arrange
        Guid tripId = await PutTripAsync(false);
        var bookingId = Guid.NewGuid();
        var otherBooking = Guid.NewGuid();
        await _store.PutHeldSeatAsync(tripId, "3A", bookingId);
        await _store.PutHeldSeatAsync(tripId, "3C", otherBooking);

        // Act
        HttpResponseMessage response = await _store.ReleaseAsync(tripId, bookingId);

        // Assert
        ReleaseHoldResponse release = await ReleaseHoldSeatStore.ReadReleaseAsync(response);
        Assert.Equal(new[] { "3A" }, release.ReleasedSeatNos);
        Dictionary<string, string> seat3C = await _store.GetStoredSeatAsync(tripId, "3C");
        Assert.Equal("Held", seat3C["status"]);
        Assert.Equal(otherBooking.ToString(), seat3C["bookingId"]);
    }

    [Fact]
    public async Task ReleaseHold_SeatsAlreadyBooked_RefusedHoldAlreadyConfirmedAndStaysBooked()
    {
        // Arrange
        Guid tripId = await PutTripAsync(false);
        var bookingId = Guid.NewGuid();
        await _store.PutBookedSeatAsync(tripId, "4C", bookingId);

        // Act
        HttpResponseMessage response = await _store.ReleaseAsync(tripId, bookingId);

        // Assert
        ReleaseHoldProblem problem = await ReleaseHoldSeatStore.ReadProblemAsync(response);
        Assert.Equal(HttpStatusCode.Conflict, problem.Status);
        Assert.Equal("HOLD_ALREADY_CONFIRMED", problem.Code);
        Assert.Equal(new[] { "4C" }, problem.NamedSeats);
        Dictionary<string, string> seat = await _store.GetStoredSeatAsync(tripId, "4C");
        Assert.Equal("Booked", seat["status"]);
        Assert.Equal(bookingId.ToString(), seat["bookingId"]);
    }

    [Fact]
    public async Task ReleaseHold_CancelledTrip_BlocksTheSeatsInsteadOfFreeingThem()
    {
        // Arrange
        Guid tripId = await PutTripAsync(true);
        var bookingId = Guid.NewGuid();
        await _store.PutHeldSeatAsync(tripId, "3A", bookingId);

        // Act
        HttpResponseMessage response = await _store.ReleaseAsync(tripId, bookingId);

        // Assert
        ReleaseHoldResponse release = await ReleaseHoldSeatStore.ReadReleaseAsync(response);
        Assert.Equal(new[] { "3A" }, release.ReleasedSeatNos);
        Dictionary<string, string> seat = await _store.GetStoredSeatAsync(tripId, "3A");
        Assert.Equal("Blocked", seat["status"]);
        Assert.Equal("TripCancelled", seat["blockReason"]);
        Assert.Equal("HoldReleased", seat["lastChangeReason"]);
        Assert.False(seat.ContainsKey("bookingId"));
    }

    [Fact]
    public async Task ReleaseHold_UnknownTrip_RefusedNotFound()
    {
        // Act
        HttpResponseMessage response = await _store.ReleaseAsync(Guid.NewGuid(), Guid.NewGuid());

        // Assert
        ReleaseHoldProblem problem = await ReleaseHoldSeatStore.ReadProblemAsync(response);
        Assert.Equal(HttpStatusCode.NotFound, problem.Status);
        Assert.Equal("NOT_FOUND", problem.Code);
    }

    [Theory]
    [InlineData("not-a-trip", "3f1c2e4a-5b6d-4e7f-8a9b-0c1d2e3f4a5b", "tripId")]
    [InlineData("3f1c2e4a-5b6d-4e7f-8a9b-0c1d2e3f4a5b", "B-77", "bookingId")]
    public async Task ReleaseHold_MalformedIdentifier_RefusedValidationFailedNamingIt(string tripId, string bookingId, string field)
    {
        // Arrange
        HttpRequestMessage request = ReleaseHoldSeatStore.ReleaseRequestWith(tripId, bookingId, ReleaseHoldSeatStore.InternalKey, null);

        // Act
        HttpResponseMessage response = await _store.SendAsync(request);

        // Assert
        ReleaseHoldProblem problem = await ReleaseHoldSeatStore.ReadProblemAsync(response);
        Assert.Equal(HttpStatusCode.BadRequest, problem.Status);
        Assert.Equal("VALIDATION_FAILED", problem.Code);
        Assert.Contains(field, problem.ErrorFields);
    }

    [Theory]
    [MemberData(nameof(MissingOrWrongCredentials))]
    public async Task ReleaseHold_NoKeyWrongKeyOrOnlyAnAccessToken_RefusedUnauthorizedAndNothingChanges(
        string? internalKey, string? accessToken)
    {
        // Arrange
        Guid tripId = await PutTripAsync(false);
        var bookingId = Guid.NewGuid();
        await _store.PutHeldSeatAsync(tripId, "3A", bookingId);
        HttpRequestMessage request = ReleaseHoldSeatStore.ReleaseRequestWith(
            tripId.ToString(), bookingId.ToString(), internalKey, accessToken);

        // Act
        HttpResponseMessage response = await _store.SendAsync(request);

        // Assert
        ReleaseHoldProblem problem = await ReleaseHoldSeatStore.ReadProblemAsync(response);
        Assert.Equal(HttpStatusCode.Unauthorized, problem.Status);
        Assert.Equal("UNAUTHORIZED", problem.Code);
        Dictionary<string, string> seat = await _store.GetStoredSeatAsync(tripId, "3A");
        Assert.Equal("Held", seat["status"]);
    }

    private async Task<Guid> PutTripAsync(bool isCancelled)
    {
        var tripId = Guid.NewGuid();
        await _store.PutTripAsync(tripId, _departure, isCancelled);
        return tripId;
    }
}
