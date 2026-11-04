namespace Api.Features.Holds.CancelHold;

public sealed class CancelHoldTests : IClassFixture<CancelHoldSeatStore>
{
    private const string Note = "Expiry stuck, ticket #4521";

    private static readonly DateTimeOffset _departure = CancelHoldSeatStore.Now.AddHours(6);

    private readonly CancelHoldSeatStore _store;

    public CancelHoldTests(CancelHoldSeatStore store)
    {
        _store = store;
    }

    public static IEnumerable<object?[]> InvalidNotes()
    {
        yield return new object?[] { null };
        yield return new object?[] { "123456789" };
        yield return new object?[] { new string('x', 501) };
        yield return new object?[] { new string(' ', 10) };
    }

    [Fact]
    public async Task CancelHold_StuckHold_MakesSeatsAvailableAndRecordsTheAdminAndNote()
    {
        // Arrange
        Guid tripId = await PutTripAsync(false);
        var bookingId = Guid.NewGuid();
        var adminUserId = Guid.NewGuid();
        await _store.PutHeldSeatAsync(tripId, "3A", bookingId);
        await _store.PutHeldSeatAsync(tripId, "3B", bookingId);
        HttpRequestMessage request = CancelHoldSeatStore.CancelRequestWith(
            tripId.ToString(), bookingId.ToString(), CancelHoldSeatStore.TokenFor("Admin", adminUserId), Note);
        request.Headers.Add("X-Correlation-Id", "corr-cancel-ac1");

        // Act
        HttpResponseMessage response = await _store.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        CancelHoldResponse cancel = await CancelHoldSeatStore.ReadCancelAsync(response);
        Assert.Equal(new[] { "3A", "3B" }, cancel.ReleasedSeatNos);
        Dictionary<string, string> seat3A = await _store.GetStoredSeatAsync(tripId, "3A");
        Dictionary<string, string> seat3B = await _store.GetStoredSeatAsync(tripId, "3B");
        Assert.Equal("Available", seat3A["status"]);
        Assert.False(seat3A.ContainsKey("bookingId"));
        Assert.False(seat3A.ContainsKey("holdExpiresAt"));
        Assert.Equal("AdminReleased", seat3A["lastChangeReason"]);
        Assert.Equal(adminUserId.ToString(), seat3A["lastChangedBy"]);
        Assert.Equal(Note, seat3A["lastChangeNote"]);
        Assert.Equal("corr-cancel-ac1", seat3A["lastChangeCorrelationId"]);
        Assert.Equal(CancelHoldSeatStore.Now.UtcDateTime.ToString("O"), seat3A["lastChangedAt"]);
        Assert.Equal("Available", seat3B["status"]);
        Assert.Equal(seat3A["lastChangeRequest"], seat3B["lastChangeRequest"]);
    }

    [Fact]
    public async Task CancelHold_SeatsBooked_RefusedHoldAlreadyConfirmedAndStaysBooked()
    {
        // Arrange
        Guid tripId = await PutTripAsync(false);
        var bookingId = Guid.NewGuid();
        await _store.PutBookedSeatAsync(tripId, "4C", bookingId);

        // Act
        HttpResponseMessage response = await _store.CancelAsync(tripId, bookingId, Note);

        // Assert
        CancelHoldProblem problem = await CancelHoldSeatStore.ReadProblemAsync(response);
        Assert.Equal(HttpStatusCode.Conflict, problem.Status);
        Assert.Equal("HOLD_ALREADY_CONFIRMED", problem.Code);
        Assert.Equal(new[] { "4C" }, problem.NamedSeats);
        Dictionary<string, string> seat = await _store.GetStoredSeatAsync(tripId, "4C");
        Assert.Equal("Booked", seat["status"]);
        Assert.Equal(bookingId.ToString(), seat["bookingId"]);
        Assert.Equal("seed", seat["lastChangeRequest"]);
    }

    [Fact]
    public async Task CancelHold_BookingHoldsNothing_RefusedHoldNotActive()
    {
        // Arrange
        Guid tripId = await PutTripAsync(false);

        // Act
        HttpResponseMessage response = await _store.CancelAsync(tripId, Guid.NewGuid(), Note);

        // Assert
        CancelHoldProblem problem = await CancelHoldSeatStore.ReadProblemAsync(response);
        Assert.Equal(HttpStatusCode.Conflict, problem.Status);
        Assert.Equal("HOLD_NOT_ACTIVE", problem.Code);
    }

    [Fact]
    public async Task CancelHold_CancelledTwice_SecondRefusedHoldNotActiveAndChangesNothing()
    {
        // Arrange
        Guid tripId = await PutTripAsync(false);
        var bookingId = Guid.NewGuid();
        await _store.PutHeldSeatAsync(tripId, "3A", bookingId);
        await _store.CancelAsync(tripId, bookingId, Note);
        Dictionary<string, string> afterFirst = await _store.GetStoredSeatAsync(tripId, "3A");

        // Act
        HttpResponseMessage response = await _store.CancelAsync(tripId, bookingId, "A second note, ticket #4522");

        // Assert
        CancelHoldProblem problem = await CancelHoldSeatStore.ReadProblemAsync(response);
        Assert.Equal("HOLD_NOT_ACTIVE", problem.Code);
        Dictionary<string, string> afterSecond = await _store.GetStoredSeatAsync(tripId, "3A");
        Assert.Equal(afterFirst["lastChangeRequest"], afterSecond["lastChangeRequest"]);
        Assert.Equal(Note, afterSecond["lastChangeNote"]);
    }

    [Fact]
    public async Task CancelHold_CancelledTrip_BlocksTheSeatsInsteadOfFreeingThem()
    {
        // Arrange
        Guid tripId = await PutTripAsync(true);
        var bookingId = Guid.NewGuid();
        await _store.PutHeldSeatAsync(tripId, "3A", bookingId);

        // Act
        HttpResponseMessage response = await _store.CancelAsync(tripId, bookingId, Note);

        // Assert
        CancelHoldResponse cancel = await CancelHoldSeatStore.ReadCancelAsync(response);
        Assert.Equal(new[] { "3A" }, cancel.ReleasedSeatNos);
        Dictionary<string, string> seat = await _store.GetStoredSeatAsync(tripId, "3A");
        Assert.Equal("Blocked", seat["status"]);
        Assert.Equal("TripCancelled", seat["blockReason"]);
        Assert.Equal("AdminReleased", seat["lastChangeReason"]);
        Assert.Equal(Note, seat["lastChangeNote"]);
        Assert.False(seat.ContainsKey("bookingId"));
    }

    [Fact]
    public async Task CancelHold_AnotherBookingsSeat_IsLeftAlone()
    {
        // Arrange
        Guid tripId = await PutTripAsync(false);
        var bookingId = Guid.NewGuid();
        var otherBooking = Guid.NewGuid();
        await _store.PutHeldSeatAsync(tripId, "3A", bookingId);
        await _store.PutHeldSeatAsync(tripId, "3C", otherBooking);

        // Act
        HttpResponseMessage response = await _store.CancelAsync(tripId, bookingId, Note);

        // Assert
        CancelHoldResponse cancel = await CancelHoldSeatStore.ReadCancelAsync(response);
        Assert.Equal(new[] { "3A" }, cancel.ReleasedSeatNos);
        Dictionary<string, string> seat3C = await _store.GetStoredSeatAsync(tripId, "3C");
        Assert.Equal("Held", seat3C["status"]);
        Assert.Equal(otherBooking.ToString(), seat3C["bookingId"]);
    }

    [Theory]
    [MemberData(nameof(InvalidNotes))]
    public async Task CancelHold_MissingShortLongOrBlankNote_RefusedValidationFailedNamingNoteAndNothingChanges(string? note)
    {
        // Arrange
        Guid tripId = await PutTripAsync(false);
        var bookingId = Guid.NewGuid();
        await _store.PutHeldSeatAsync(tripId, "3A", bookingId);
        HttpRequestMessage request = CancelHoldSeatStore.CancelRequestWith(
            tripId.ToString(), bookingId.ToString(), CancelHoldSeatStore.AdminToken(), note);

        // Act
        HttpResponseMessage response = await _store.SendAsync(request);

        // Assert
        CancelHoldProblem problem = await CancelHoldSeatStore.ReadProblemAsync(response);
        Assert.Equal(HttpStatusCode.BadRequest, problem.Status);
        Assert.Equal("VALIDATION_FAILED", problem.Code);
        Assert.Contains("note", problem.ErrorFields);
        Dictionary<string, string> seat = await _store.GetStoredSeatAsync(tripId, "3A");
        Assert.Equal("Held", seat["status"]);
    }

    [Theory]
    [InlineData("not-a-trip", "3f1c2e4a-5b6d-4e7f-8a9b-0c1d2e3f4a5b", "tripId")]
    [InlineData("3f1c2e4a-5b6d-4e7f-8a9b-0c1d2e3f4a5b", "B-77", "bookingId")]
    public async Task CancelHold_MalformedIdentifier_RefusedValidationFailedNamingIt(string tripId, string bookingId, string field)
    {
        // Arrange
        HttpRequestMessage request = CancelHoldSeatStore.CancelRequestWith(tripId, bookingId, CancelHoldSeatStore.AdminToken(), Note);

        // Act
        HttpResponseMessage response = await _store.SendAsync(request);

        // Assert
        CancelHoldProblem problem = await CancelHoldSeatStore.ReadProblemAsync(response);
        Assert.Equal(HttpStatusCode.BadRequest, problem.Status);
        Assert.Equal("VALIDATION_FAILED", problem.Code);
        Assert.Contains(field, problem.ErrorFields);
    }

    [Theory]
    [InlineData("Operator")]
    [InlineData("Customer")]
    public async Task CancelHold_OperatorOrCustomer_RefusedForbiddenAndNothingChanges(string role)
    {
        // Arrange
        Guid tripId = await PutTripAsync(false);
        var bookingId = Guid.NewGuid();
        await _store.PutHeldSeatAsync(tripId, "3A", bookingId);
        HttpRequestMessage request = CancelHoldSeatStore.CancelRequestWith(
            tripId.ToString(), bookingId.ToString(), CancelHoldSeatStore.TokenFor(role, Guid.NewGuid()), Note);

        // Act
        HttpResponseMessage response = await _store.SendAsync(request);

        // Assert
        CancelHoldProblem problem = await CancelHoldSeatStore.ReadProblemAsync(response);
        Assert.Equal(HttpStatusCode.Forbidden, problem.Status);
        Assert.Equal("FORBIDDEN", problem.Code);
        Dictionary<string, string> seat = await _store.GetStoredSeatAsync(tripId, "3A");
        Assert.Equal("Held", seat["status"]);
    }

    [Fact]
    public async Task CancelHold_NoAccessToken_RefusedUnauthorizedAndNothingChanges()
    {
        // Arrange
        Guid tripId = await PutTripAsync(false);
        var bookingId = Guid.NewGuid();
        await _store.PutHeldSeatAsync(tripId, "3A", bookingId);
        HttpRequestMessage request = CancelHoldSeatStore.CancelRequestWith(tripId.ToString(), bookingId.ToString(), null, Note);

        // Act
        HttpResponseMessage response = await _store.SendAsync(request);

        // Assert
        CancelHoldProblem problem = await CancelHoldSeatStore.ReadProblemAsync(response);
        Assert.Equal(HttpStatusCode.Unauthorized, problem.Status);
        Assert.Equal("UNAUTHORIZED", problem.Code);
        Dictionary<string, string> seat = await _store.GetStoredSeatAsync(tripId, "3A");
        Assert.Equal("Held", seat["status"]);
    }

    [Fact]
    public async Task CancelHold_UnknownTrip_RefusedNotFound()
    {
        // Act
        HttpResponseMessage response = await _store.CancelAsync(Guid.NewGuid(), Guid.NewGuid(), Note);

        // Assert
        CancelHoldProblem problem = await CancelHoldSeatStore.ReadProblemAsync(response);
        Assert.Equal(HttpStatusCode.NotFound, problem.Status);
        Assert.Equal("NOT_FOUND", problem.Code);
    }

    private async Task<Guid> PutTripAsync(bool isCancelled)
    {
        var tripId = Guid.NewGuid();
        await _store.PutTripAsync(tripId, _departure, isCancelled);
        return tripId;
    }
}
