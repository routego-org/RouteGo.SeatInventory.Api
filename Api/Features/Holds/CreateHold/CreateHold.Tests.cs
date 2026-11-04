namespace Api.Features.Holds.CreateHold;

public sealed class CreateHoldTests : IClassFixture<CreateHoldSeatStore>
{
    private static readonly DateTimeOffset _departure = CreateHoldSeatStore.Now.AddHours(6);

    private readonly CreateHoldSeatStore _store;

    public CreateHoldTests(CreateHoldSeatStore store)
    {
        _store = store;
    }

    public static IEnumerable<object[]> MalformedRequests()
    {
        string bookingId = Guid.NewGuid().ToString();
        yield return new object[] { new { bookingId, seatNos = new List<string>() }, "seatNos" };
        yield return new object[] { new { bookingId, seatNos = new[] { "1A", "1B", "1C", "1D", "1E", "1F", "2A" } }, "seatNos" };
        yield return new object[] { new { bookingId, seatNos = new[] { "3A", "3A" } }, "seatNos" };
        yield return new object[] { new { seatNos = new[] { "3A" } }, "bookingId" };
        yield return new object[] { new { bookingId = "B-77", seatNos = new[] { "3A" } }, "bookingId" };
        yield return new object[] { new { bookingId, seatNos = new[] { "3A" }, holdSeconds = 0 }, "holdSeconds" };
    }

    public static IEnumerable<object?[]> MissingOrWrongCredentials()
    {
        yield return new object?[] { null, null };
        yield return new object?[] { "not-the-internal-key", null };
        yield return new object?[] { null, CreateHoldSeatStore.AdminToken() };
    }

    [Fact]
    public async Task CreateHold_FreeSeatsWithoutLength_HoldsThemForTenMinutesAndStartsOneTimer()
    {
        // Arrange
        Guid tripId = await PutOpenTripAsync("3A", "3B");
        var bookingId = Guid.NewGuid();
        HttpRequestMessage request = CreateHoldSeatStore.HoldRequestWith(
            tripId.ToString(), Hold(bookingId, "3A", "3B"), CreateHoldSeatStore.InternalKey, null);
        request.Headers.Add("X-Correlation-Id", "corr-hold-ac1");

        // Act
        HttpResponseMessage response = await _store.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal($"/internal/trips/{tripId}/holds/{bookingId}", response.Headers.Location!.OriginalString);
        CreateHoldResponse hold = await CreateHoldSeatStore.ReadHoldAsync(response);
        DateTimeOffset expiry = CreateHoldSeatStore.Now.AddSeconds(600);
        Assert.Equal(HoldStatus.Held, hold.Status);
        Assert.Equal(expiry, hold.HoldExpiresAt);
        Dictionary<string, string> seat3A = await _store.GetStoredSeatAsync(tripId, "3A");
        Dictionary<string, string> seat3B = await _store.GetStoredSeatAsync(tripId, "3B");
        Assert.Equal("Held", seat3A["status"]);
        Assert.Equal(bookingId.ToString(), seat3A["bookingId"]);
        Assert.Equal(expiry, DateTimeOffset.Parse(seat3A["holdExpiresAt"], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("Held", seat3A["lastChangeReason"]);
        Assert.Equal("corr-hold-ac1", seat3A["lastChangeCorrelationId"]);
        Assert.Equal("Held", seat3B["status"]);
        Assert.Equal(seat3A["lastChangeRequest"], seat3B["lastChangeRequest"]);
        CreateHoldSentTimer timer = Assert.Single(_store.Queue!.TimersFor(bookingId));
        Assert.Equal(600, timer.DelaySeconds);
        Assert.Equal(tripId, timer.Timer.TripId);
        Assert.Equal(seat3A["holdExpiresAt"], timer.Timer.HoldExpiresAt);
        Assert.Equal(new[] { "3A", "3B" }, timer.Timer.SeatNos);
        Assert.Equal("corr-hold-ac1", timer.Timer.CorrelationId);
    }

    [Fact]
    public async Task CreateHold_HundredRequestsForOneSeatAtOnce_ExactlyOneIsHeld()
    {
        // Arrange
        Guid tripId = await PutOpenTripAsync("1A");
        var requests = Enumerable.Range(0, 100).Select(_ => Hold(Guid.NewGuid(), "1A")).ToList();

        // Act
        HttpResponseMessage[] responses = await _store.HoldAllAtOnceAsync(tripId.ToString(), requests);

        // Assert
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
        Assert.Equal(99, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));
        HttpResponseMessage created = responses.First(response => response.StatusCode == HttpStatusCode.Created);
        HttpResponseMessage refused = responses.First(response => response.StatusCode == HttpStatusCode.Conflict);
        CreateHoldResponse winner = await CreateHoldSeatStore.ReadHoldAsync(created);
        CreateHoldProblem loser = await CreateHoldSeatStore.ReadProblemAsync(refused, "unavailableSeats");
        Assert.Equal("SEATS_UNAVAILABLE", loser.Code);
        Assert.Equal(new[] { "1A" }, loser.NamedSeats);
        Dictionary<string, string> seat = await _store.GetStoredSeatAsync(tripId, "1A");
        Assert.Equal(winner.BookingId.ToString(), seat["bookingId"]);
    }

    [Fact]
    public async Task CreateHold_OneSeatHeldByAnotherBooking_HoldsNothingAndStartsNoTimer()
    {
        // Arrange
        Guid tripId = await PutOpenTripAsync("3A", "3C");
        await _store.PutSeatAsync(tripId, "3B", "Held", Guid.NewGuid());
        var bookingId = Guid.NewGuid();

        // Act
        HttpResponseMessage response = await _store.HoldAsync(tripId.ToString(), Hold(bookingId, "3A", "3B", "3C"));

        // Assert
        CreateHoldProblem problem = await CreateHoldSeatStore.ReadProblemAsync(response, "unavailableSeats");
        Assert.Equal(HttpStatusCode.Conflict, problem.Status);
        Assert.Equal("SEATS_UNAVAILABLE", problem.Code);
        Assert.Equal(new[] { "3B" }, problem.NamedSeats);
        Dictionary<string, string> seat3A = await _store.GetStoredSeatAsync(tripId, "3A");
        Dictionary<string, string> seat3C = await _store.GetStoredSeatAsync(tripId, "3C");
        Assert.Equal("Available", seat3A["status"]);
        Assert.Equal("seed", seat3A["lastChangeRequest"]);
        Assert.Equal("Available", seat3C["status"]);
        Assert.Empty(_store.Queue!.TimersFor(bookingId));
    }

    [Fact]
    public async Task CreateHold_BookedAndBlockedSeats_NamesEveryUnavailableSeat()
    {
        // Arrange
        Guid tripId = await PutOpenTripAsync("3A");
        await _store.PutSeatAsync(tripId, "3B", "Booked", Guid.NewGuid());
        await _store.PutSeatAsync(tripId, "L1", "Blocked", null);

        // Act
        HttpResponseMessage response = await _store.HoldAsync(tripId.ToString(), Hold(Guid.NewGuid(), "3A", "3B", "L1"));

        // Assert
        CreateHoldProblem problem = await CreateHoldSeatStore.ReadProblemAsync(response, "unavailableSeats");
        Assert.Equal("SEATS_UNAVAILABLE", problem.Code);
        Assert.Equal(new[] { "3B", "L1" }, problem.NamedSeats);
    }

    [Fact]
    public async Task CreateHold_FifteenSecondLength_ExpiresFifteenSecondsLater()
    {
        // Arrange
        Guid tripId = await PutOpenTripAsync("3A");
        var bookingId = Guid.NewGuid();

        // Act
        HttpResponseMessage response = await _store.HoldAsync(tripId.ToString(), HoldFor(bookingId, 15, "3A"));

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Dictionary<string, string> seat = await _store.GetStoredSeatAsync(tripId, "3A");
        DateTimeOffset expiry = CreateHoldSeatStore.Now.AddSeconds(15);
        Assert.Equal(expiry, DateTimeOffset.Parse(seat["holdExpiresAt"], System.Globalization.CultureInfo.InvariantCulture));
        CreateHoldSentTimer timer = Assert.Single(_store.Queue!.TimersFor(bookingId));
        Assert.Equal(15, timer.DelaySeconds);
    }

    [Fact]
    public async Task CreateHold_LengthAboveCeiling_RefusedHoldTooLongAndNothingHeld()
    {
        // Arrange
        Guid tripId = await PutOpenTripAsync("3A");
        var bookingId = Guid.NewGuid();

        // Act
        HttpResponseMessage response = await _store.HoldAsync(tripId.ToString(), HoldFor(bookingId, 3600, "3A"));

        // Assert
        CreateHoldProblem problem = await CreateHoldSeatStore.ReadProblemAsync(response, "maxHoldSeconds");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, problem.Status);
        Assert.Equal("HOLD_TOO_LONG", problem.Code);
        Assert.Equal(900, problem.Extension!.Value.GetInt32());
        Dictionary<string, string> seat = await _store.GetStoredSeatAsync(tripId, "3A");
        Assert.Equal("Available", seat["status"]);
        Assert.Empty(_store.Queue!.TimersFor(bookingId));
    }

    [Fact]
    public async Task CreateHold_SameRequestRepeatedInAnotherOrder_ReturnsTheExistingHoldUnchanged()
    {
        // Arrange
        Guid tripId = await PutOpenTripAsync("3A", "3B");
        var bookingId = Guid.NewGuid();
        await _store.HoldAsync(tripId.ToString(), Hold(bookingId, "3A", "3B"));
        Dictionary<string, string> afterFirst = await _store.GetStoredSeatAsync(tripId, "3A");

        // Act
        HttpResponseMessage response = await _store.HoldAsync(tripId.ToString(), HoldFor(bookingId, 15, "3B", "3A"));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        CreateHoldResponse hold = await CreateHoldSeatStore.ReadHoldAsync(response);
        Assert.Equal(HoldStatus.Held, hold.Status);
        Assert.Equal(CreateHoldSeatStore.Now.AddSeconds(600), hold.HoldExpiresAt);
        Dictionary<string, string> afterRepeat = await _store.GetStoredSeatAsync(tripId, "3A");
        Assert.Equal(afterFirst["holdExpiresAt"], afterRepeat["holdExpiresAt"]);
        Assert.Equal(afterFirst["lastChangeRequest"], afterRepeat["lastChangeRequest"]);
        Assert.Single(_store.Queue!.TimersFor(bookingId));
    }

    [Fact]
    public async Task CreateHold_SameSeatsAlreadyBooked_ReturnsTheBookedHold()
    {
        // Arrange
        Guid tripId = await PutOpenTripAsync();
        var bookingId = Guid.NewGuid();
        await _store.PutSeatAsync(tripId, "3A", "Booked", bookingId);
        await _store.PutSeatAsync(tripId, "3B", "Booked", bookingId);

        // Act
        HttpResponseMessage response = await _store.HoldAsync(tripId.ToString(), Hold(bookingId, "3A", "3B"));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        CreateHoldResponse hold = await CreateHoldSeatStore.ReadHoldAsync(response);
        Assert.Equal(HoldStatus.Booked, hold.Status);
        Assert.Null(hold.HoldExpiresAt);
        Assert.Empty(_store.Queue!.TimersFor(bookingId));
    }

    [Fact]
    public async Task CreateHold_BookingHoldsOtherSeats_RefusedBookingAlreadyHasSeats()
    {
        // Arrange
        Guid tripId = await PutOpenTripAsync("3A", "3B", "3C");
        var bookingId = Guid.NewGuid();
        await _store.HoldAsync(tripId.ToString(), Hold(bookingId, "3A", "3B"));

        // Act
        HttpResponseMessage response = await _store.HoldAsync(tripId.ToString(), Hold(bookingId, "3A", "3B", "3C"));

        // Assert
        CreateHoldProblem problem = await CreateHoldSeatStore.ReadProblemAsync(response, "holdStatus");
        Assert.Equal(HttpStatusCode.Conflict, problem.Status);
        Assert.Equal("BOOKING_ALREADY_HAS_SEATS", problem.Code);
        Assert.Equal(new[] { "3A", "3B" }, problem.NamedSeats);
        Assert.Equal("held", problem.Extension!.Value.GetString());
        Dictionary<string, string> seat3C = await _store.GetStoredSeatAsync(tripId, "3C");
        Assert.Equal("Available", seat3C["status"]);
    }

    [Fact]
    public async Task CreateHold_BookingOwnsOtherBookedSeats_RefusedBookingAlreadyHasSeats()
    {
        // Arrange
        Guid tripId = await PutOpenTripAsync("3C");
        var bookingId = Guid.NewGuid();
        await _store.PutSeatAsync(tripId, "3A", "Booked", bookingId);

        // Act
        HttpResponseMessage response = await _store.HoldAsync(tripId.ToString(), Hold(bookingId, "3C"));

        // Assert
        CreateHoldProblem problem = await CreateHoldSeatStore.ReadProblemAsync(response, "holdStatus");
        Assert.Equal("BOOKING_ALREADY_HAS_SEATS", problem.Code);
        Assert.Equal(new[] { "3A" }, problem.NamedSeats);
        Assert.Equal("booked", problem.Extension!.Value.GetString());
    }

    [Fact]
    public async Task CreateHold_TwentyMinutesAfterDeparture_RefusedTripDepartedWithDepartureTime()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        DateTimeOffset departedAt = CreateHoldSeatStore.Now.AddMinutes(-20);
        await _store.PutTripAsync(tripId, departedAt, false);
        await _store.PutSeatAsync(tripId, "3A", "Available", null);

        // Act
        HttpResponseMessage response = await _store.HoldAsync(tripId.ToString(), Hold(Guid.NewGuid(), "3A"));

        // Assert
        CreateHoldProblem problem = await CreateHoldSeatStore.ReadProblemAsync(response, "departedAt");
        Assert.Equal(HttpStatusCode.Conflict, problem.Status);
        Assert.Equal("TRIP_DEPARTED", problem.Code);
        Assert.Equal(departedAt, problem.Extension!.Value.GetDateTimeOffset());
    }

    [Fact]
    public async Task CreateHold_TwoMinutesBeforeDeparture_IsHeld()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        await _store.PutTripAsync(tripId, CreateHoldSeatStore.Now.AddMinutes(2), false);
        await _store.PutSeatAsync(tripId, "3A", "Available", null);

        // Act
        HttpResponseMessage response = await _store.HoldAsync(tripId.ToString(), Hold(Guid.NewGuid(), "3A"));

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CreateHold_CancelledTrip_RefusedTripCancelled()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        await _store.PutTripAsync(tripId, _departure, true);
        await _store.PutSeatAsync(tripId, "3A", "Available", null);

        // Act
        HttpResponseMessage response = await _store.HoldAsync(tripId.ToString(), Hold(Guid.NewGuid(), "3A"));

        // Assert
        CreateHoldProblem problem = await CreateHoldSeatStore.ReadProblemAsync(response, "code");
        Assert.Equal(HttpStatusCode.Conflict, problem.Status);
        Assert.Equal("TRIP_CANCELLED", problem.Code);
    }

    [Theory]
    [MemberData(nameof(MalformedRequests))]
    public async Task CreateHold_MalformedRequest_RefusedValidationFailedNamingTheField(object body, string field)
    {
        // Arrange
        Guid tripId = await PutOpenTripAsync("3A");

        // Act
        HttpResponseMessage response = await _store.HoldAsync(tripId.ToString(), body);

        // Assert
        CreateHoldProblem problem = await CreateHoldSeatStore.ReadProblemAsync(response, "code");
        Assert.Equal(HttpStatusCode.BadRequest, problem.Status);
        Assert.Equal("VALIDATION_FAILED", problem.Code);
        Assert.Contains(field, problem.ErrorFields);
    }

    [Fact]
    public async Task CreateHold_SeatTheTripDoesNotHave_RefusedUnknownSeats()
    {
        // Arrange
        Guid tripId = await PutOpenTripAsync("3A");

        // Act
        HttpResponseMessage response = await _store.HoldAsync(tripId.ToString(), Hold(Guid.NewGuid(), "3A", "Z99"));

        // Assert
        CreateHoldProblem problem = await CreateHoldSeatStore.ReadProblemAsync(response, "unknownSeats");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, problem.Status);
        Assert.Equal("UNKNOWN_SEATS", problem.Code);
        Assert.Equal(new[] { "Z99" }, problem.NamedSeats);
    }

    [Fact]
    public async Task CreateHold_UnknownTrip_RefusedNotFound()
    {
        // Act
        HttpResponseMessage response = await _store.HoldAsync(Guid.NewGuid().ToString(), Hold(Guid.NewGuid(), "3A"));

        // Assert
        CreateHoldProblem problem = await CreateHoldSeatStore.ReadProblemAsync(response, "code");
        Assert.Equal(HttpStatusCode.NotFound, problem.Status);
        Assert.Equal("NOT_FOUND", problem.Code);
    }

    [Theory]
    [MemberData(nameof(MissingOrWrongCredentials))]
    public async Task CreateHold_NoKeyWrongKeyOrOnlyAnAccessToken_RefusedUnauthorizedAndNothingChanges(
        string? internalKey, string? accessToken)
    {
        // Arrange
        Guid tripId = await PutOpenTripAsync("3A");
        HttpRequestMessage request = CreateHoldSeatStore.HoldRequestWith(
            tripId.ToString(), Hold(Guid.NewGuid(), "3A"), internalKey, accessToken);

        // Act
        HttpResponseMessage response = await _store.SendAsync(request);

        // Assert
        CreateHoldProblem problem = await CreateHoldSeatStore.ReadProblemAsync(response, "code");
        Assert.Equal(HttpStatusCode.Unauthorized, problem.Status);
        Assert.Equal("UNAUTHORIZED", problem.Code);
        Dictionary<string, string> seat = await _store.GetStoredSeatAsync(tripId, "3A");
        Assert.Equal("Available", seat["status"]);
    }

    [Fact]
    public async Task CreateHold_TimerCannotBeStarted_FailsAndHoldsNothing()
    {
        // Arrange
        Guid tripId = await PutOpenTripAsync("3A");
        var bookingId = Guid.NewGuid();
        _store.Queue!.MakeUnreachableFor(bookingId);

        // Act
        HttpResponseMessage response = await _store.HoldAsync(tripId.ToString(), Hold(bookingId, "3A"));

        // Assert
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Dictionary<string, string> seat = await _store.GetStoredSeatAsync(tripId, "3A");
        Assert.Equal("Available", seat["status"]);
    }

    [Fact]
    public async Task CreateHold_NoCorrelationIdSent_OneIsMadeForTheSeatsAndTheTimer()
    {
        // Arrange
        Guid tripId = await PutOpenTripAsync("3A");
        var bookingId = Guid.NewGuid();

        // Act
        await _store.HoldAsync(tripId.ToString(), Hold(bookingId, "3A"));

        // Assert
        Dictionary<string, string> seat = await _store.GetStoredSeatAsync(tripId, "3A");
        Assert.True(Guid.TryParse(seat["lastChangeCorrelationId"], out _));
        CreateHoldSentTimer timer = Assert.Single(_store.Queue!.TimersFor(bookingId));
        Assert.Equal(seat["lastChangeCorrelationId"], timer.Timer.CorrelationId);
    }

    [Fact]
    public async Task CreateHold_OneSecondHold_TimerArrivesOnTheQueueAtItsExpiry()
    {
        // Arrange
        Guid tripId = await PutOpenTripAsync("3A");
        var bookingId = Guid.NewGuid();

        // Act
        await _store.HoldAsync(tripId.ToString(), HoldFor(bookingId, 1, "3A"));

        // Assert
        HoldExpiryTimer? timer = await _store.ReceiveTimerAsync(bookingId, TimeSpan.FromSeconds(20));
        Assert.NotNull(timer);
        Assert.Equal(tripId, timer.TripId);
        Assert.Equal(new[] { "3A" }, timer.SeatNos);
    }

    private static object Hold(Guid bookingId, params string[] seatNos)
    {
        return new { bookingId, seatNos };
    }

    private static object HoldFor(Guid bookingId, int holdSeconds, params string[] seatNos)
    {
        return new { bookingId, seatNos, holdSeconds };
    }

    private async Task<Guid> PutOpenTripAsync(params string[] availableSeatNos)
    {
        var tripId = Guid.NewGuid();
        await _store.PutTripAsync(tripId, _departure, false);
        foreach (string seatNo in availableSeatNos)
        {
            await _store.PutSeatAsync(tripId, seatNo, "Available", null);
        }

        return tripId;
    }
}
