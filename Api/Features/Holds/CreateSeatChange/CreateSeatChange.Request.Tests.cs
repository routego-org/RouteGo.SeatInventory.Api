namespace Api.Features.Holds.CreateSeatChange;

/// <summary>Requests refused before any seat is looked at: shape, identifiers, the trip, the credential.</summary>
public sealed class CreateSeatChangeRequestTests : IClassFixture<CreateSeatChangeSeatStore>
{
    private readonly CreateSeatChangeSeatStore _store;

    public CreateSeatChangeRequestTests(CreateSeatChangeSeatStore store)
    {
        _store = store;
    }

    public static IEnumerable<object[]> MalformedBodies()
    {
        yield return new object[] { new CreateSeatChangeRequest(null), "changes" };
        yield return new object[] { CreateSeatChangeSeatStore.Moves(), "changes" };
        yield return new object[] { CreateSeatChangeSeatStore.Moves("1A>2A", "1B>2B", "1C>2C", "1D>2D", "1E>2E", "1F>2F", "1G>2G"), "changes" };
        yield return new object[] { CreateSeatChangeSeatStore.Moves("4C>5C", "4D>5C"), "changes" };
        yield return new object[] { CreateSeatChangeSeatStore.Moves("4C>4C"), "changes[0].toSeatNo" };
        yield return new object[] { new CreateSeatChangeRequest(new List<RequestedSeatMove?> { null }), "changes" };
        yield return new object[] { CreateSeatChangeSeatStore.Moves("4C> "), "changes[0].toSeatNo" };
    }

    [Theory]
    [MemberData(nameof(MalformedBodies))]
    public async Task CreateSeatChange_MalformedMoves_RefusedValidationFailedNamingTheField(CreateSeatChangeRequest body, string field)
    {
        // Arrange
        HttpRequestMessage request = CreateSeatChangeSeatStore.ChangeRequestWith(
            Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), CreateSeatChangeSeatStore.InternalKey, body);

        // Act
        HttpResponseMessage response = await _store.SendAsync(request);

        // Assert
        CreateSeatChangeProblem problem = await CreateSeatChangeSeatStore.ReadProblemAsync(response);
        Assert.Equal(HttpStatusCode.BadRequest, problem.Status);
        Assert.Equal("VALIDATION_FAILED", problem.Code);
        Assert.Contains(field, problem.ErrorFields);
    }

    [Theory]
    [InlineData("not-a-trip", "3f1c2e4a-5b6d-4e7f-8a9b-0c1d2e3f4a5b", "tripId")]
    [InlineData("3f1c2e4a-5b6d-4e7f-8a9b-0c1d2e3f4a5b", "B-55", "bookingId")]
    public async Task CreateSeatChange_MalformedIdentifier_RefusedValidationFailedNamingIt(string tripId, string bookingId, string field)
    {
        // Arrange
        HttpRequestMessage request = CreateSeatChangeSeatStore.ChangeRequestWith(
            tripId, bookingId, CreateSeatChangeSeatStore.InternalKey, CreateSeatChangeSeatStore.Moves("4C>5C"));

        // Act
        HttpResponseMessage response = await _store.SendAsync(request);

        // Assert
        CreateSeatChangeProblem problem = await CreateSeatChangeSeatStore.ReadProblemAsync(response);
        Assert.Equal(HttpStatusCode.BadRequest, problem.Status);
        Assert.Contains(field, problem.ErrorFields);
    }

    [Fact]
    public async Task CreateSeatChange_UnknownTrip_RefusedNotFound()
    {
        // Act
        HttpResponseMessage response = await _store.ChangeAsync(Guid.NewGuid(), Guid.NewGuid(), "4C>5C");

        // Assert
        CreateSeatChangeProblem problem = await CreateSeatChangeSeatStore.ReadProblemAsync(response);
        Assert.Equal(HttpStatusCode.NotFound, problem.Status);
        Assert.Equal("NOT_FOUND", problem.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-the-internal-key")]
    public async Task CreateSeatChange_NoKeyOrWrongKey_RefusedUnauthorizedAndNothingChanges(string? internalKey)
    {
        // Arrange
        var tripId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();
        await _store.PutTripAsync(tripId, CreateSeatChangeSeatStore.Now.AddHours(6), false);
        await _store.PutSeatOfAsync(tripId, "4C", "Booked", bookingId);
        await _store.PutAvailableSeatAsync(tripId, "5C", "Seater");
        HttpRequestMessage request = CreateSeatChangeSeatStore.ChangeRequestWith(
            tripId.ToString(), bookingId.ToString(), internalKey, CreateSeatChangeSeatStore.Moves("4C>5C"));

        // Act
        HttpResponseMessage response = await _store.SendAsync(request);

        // Assert
        CreateSeatChangeProblem problem = await CreateSeatChangeSeatStore.ReadProblemAsync(response);
        Assert.Equal(HttpStatusCode.Unauthorized, problem.Status);
        Assert.Equal("UNAUTHORIZED", problem.Code);
        Dictionary<string, string> seat4C = await _store.GetStoredSeatAsync(tripId, "4C");
        Assert.Equal("Booked", seat4C["status"]);
    }
}
