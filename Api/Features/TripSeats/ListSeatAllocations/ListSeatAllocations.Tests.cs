namespace Api.Features.TripSeats.ListSeatAllocations;

public sealed class ListSeatAllocationsTests : IClassFixture<ListSeatAllocationsSeatStore>
{
    private static readonly Guid _operatorOne = Guid.Parse("0b6f5c9e-1f0a-4a7e-9a52-3c1d2e4f5a61");
    private static readonly Guid _operatorTwo = Guid.Parse("7d2e8a14-5b3c-4f6d-8e9a-1b2c3d4e5f60");
    private static readonly DateTimeOffset _holdExpiry = new DateTimeOffset(2026, 10, 24, 10, 10, 0, TimeSpan.Zero);

    private readonly ListSeatAllocationsSeatStore _store;

    public ListSeatAllocationsTests(ListSeatAllocationsSeatStore store)
    {
        _store = store;
    }

    [Fact]
    public async Task ListSeatAllocations_Admin_SeesHolderExpiryAndBlockReasonOfEachSeat()
    {
        // Arrange
        Guid tripId = await PutTripWithEachKindOfSeatAsync(false);

        // Act
        ListSeatAllocationsResponse allocations =
            await _store.GetAllocationsAsync(tripId, ListSeatAllocationsSeatStore.TokenFor("Admin"));

        // Assert
        Assert.Equal(_operatorOne, allocations.OperatorId);
        Assert.False(allocations.IsCancelled);
        SeatAllocation held = allocations.Items.Single(seat => seat.SeatNo == "3A");
        Assert.Equal(SeatStatus.Held, held.Status);
        Assert.Equal("B-77", held.BookingId);
        Assert.Equal(_holdExpiry, held.HoldExpiresAt);
        SeatAllocation booked = allocations.Items.Single(seat => seat.SeatNo == "4C");
        Assert.Equal(SeatStatus.Booked, booked.Status);
        Assert.Equal("B-55", booked.BookingId);
        Assert.Null(booked.HoldExpiresAt);
        SeatAllocation blocked = allocations.Items.Single(seat => seat.SeatNo == "L1");
        Assert.Equal(SeatStatus.Blocked, blocked.Status);
        Assert.Equal(BlockReason.Operator, blocked.BlockReason);
        SeatAllocation available = allocations.Items.Single(seat => seat.SeatNo == "5D");
        Assert.Equal(SeatStatus.Available, available.Status);
        Assert.Null(available.BookingId);
        Assert.Null(available.BlockReason);
    }

    [Fact]
    public async Task ListSeatAllocations_OperatorOfTheTrip_SeesTheSameAsAnAdmin()
    {
        // Arrange
        Guid tripId = await PutTripWithEachKindOfSeatAsync(false);

        // Act
        string asAdmin = await _store.GetRawAllocationsAsync(tripId, ListSeatAllocationsSeatStore.TokenFor("Admin"));
        string asOperator =
            await _store.GetRawAllocationsAsync(tripId, ListSeatAllocationsSeatStore.OperatorToken(_operatorOne));

        // Assert
        Assert.Equal(asAdmin, asOperator);
    }

    [Fact]
    public async Task ListSeatAllocations_CancelledTrip_BlocksSayTripCancelled()
    {
        // Arrange: the cancellation worker has re-blocked the operator block and the free seat.
        var tripId = Guid.NewGuid();
        await _store.PutTripAsync(tripId, _operatorOne, true);
        await _store.PutSeatAsync(tripId, ListSeatAllocationsSeatStore.Seat("L1", 1, 1, "Blocked") with { BlockReason = "TripCancelled" });
        await _store.PutSeatAsync(tripId, ListSeatAllocationsSeatStore.Seat("5D", 1, 2, "Blocked") with { BlockReason = "TripCancelled" });

        // Act
        ListSeatAllocationsResponse allocations =
            await _store.GetAllocationsAsync(tripId, ListSeatAllocationsSeatStore.TokenFor("Admin"));

        // Assert
        Assert.True(allocations.IsCancelled);
        Assert.All(allocations.Items, seat => Assert.Equal(BlockReason.TripCancelled, seat.BlockReason));
    }

    [Fact]
    public async Task ListSeatAllocations_HoldExpiryPassedBeforeTimerRan_StillShownHeldWithItsExpiry()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        DateTimeOffset expiredAt = ListSeatAllocationsSeatStore.Now.AddMinutes(-30);
        await _store.PutTripAsync(tripId, _operatorOne, false);
        await _store.PutSeatAsync(tripId, ListSeatAllocationsSeatStore.Seat("3A", 1, 1, "Held") with { BookingId = "B-77", HoldExpiresAt = expiredAt });

        // Act
        ListSeatAllocationsResponse allocations =
            await _store.GetAllocationsAsync(tripId, ListSeatAllocationsSeatStore.TokenFor("Admin"));

        // Assert
        SeatAllocation seat = Assert.Single(allocations.Items);
        Assert.Equal(SeatStatus.Held, seat.Status);
        Assert.Equal(expiredAt, seat.HoldExpiresAt);
    }

    [Fact]
    public async Task ListSeatAllocations_AnotherOperatorsTrip_RefusedForbidden()
    {
        // Arrange
        Guid tripId = await PutTripWithEachKindOfSeatAsync(false);

        // Act
        ListSeatAllocationsProblem problem =
            await _store.GetProblemAsync(tripId.ToString(), ListSeatAllocationsSeatStore.OperatorToken(_operatorTwo));

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, problem.Status);
        Assert.Equal("FORBIDDEN", problem.Code);
        Assert.Equal("urn:routego:seat-inventory:problems:forbidden", problem.Type);
    }

    [Fact]
    public async Task ListSeatAllocations_Customer_RefusedForbidden()
    {
        // Arrange
        Guid tripId = await PutTripWithEachKindOfSeatAsync(false);

        // Act
        ListSeatAllocationsProblem problem =
            await _store.GetProblemAsync(tripId.ToString(), ListSeatAllocationsSeatStore.TokenFor("Customer"));

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, problem.Status);
        Assert.Equal("FORBIDDEN", problem.Code);
    }

    [Fact]
    public async Task ListSeatAllocations_NoAccessToken_RefusedUnauthorized()
    {
        // Arrange
        Guid tripId = await PutTripWithEachKindOfSeatAsync(false);

        // Act
        ListSeatAllocationsProblem problem = await _store.GetProblemAsync(tripId.ToString(), null);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, problem.Status);
        Assert.Equal("UNAUTHORIZED", problem.Code);
        Assert.Equal("urn:routego:seat-inventory:problems:unauthorized", problem.Type);
    }

    [Fact]
    public async Task ListSeatAllocations_TokenSignedByUntrustedKey_RefusedUnauthorized()
    {
        // Arrange
        Guid tripId = await PutTripWithEachKindOfSeatAsync(false);

        // Act
        ListSeatAllocationsProblem problem =
            await _store.GetProblemAsync(tripId.ToString(), ListSeatAllocationsSeatStore.UntrustedAdminToken());

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, problem.Status);
        Assert.Equal("UNAUTHORIZED", problem.Code);
    }

    [Fact]
    public async Task ListSeatAllocations_UnknownTrip_RefusedNotFound()
    {
        // Act
        ListSeatAllocationsProblem problem =
            await _store.GetProblemAsync(Guid.NewGuid().ToString(), ListSeatAllocationsSeatStore.TokenFor("Admin"));

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, problem.Status);
        Assert.Equal("NOT_FOUND", problem.Code);
    }

    [Fact]
    public async Task ListSeatAllocations_TripIdNotAUuid_RefusedValidationFailed()
    {
        // Act
        ListSeatAllocationsProblem problem =
            await _store.GetProblemAsync("not-a-trip", ListSeatAllocationsSeatStore.TokenFor("Admin"));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, problem.Status);
        Assert.Equal("VALIDATION_FAILED", problem.Code);
    }

    private async Task<Guid> PutTripWithEachKindOfSeatAsync(bool isCancelled)
    {
        var tripId = Guid.NewGuid();
        await _store.PutTripAsync(tripId, _operatorOne, isCancelled);
        await _store.PutSeatAsync(tripId, ListSeatAllocationsSeatStore.Seat("3A", 1, 1, "Held") with { BookingId = "B-77", HoldExpiresAt = _holdExpiry });
        await _store.PutSeatAsync(tripId, ListSeatAllocationsSeatStore.Seat("4C", 1, 2, "Booked") with { BookingId = "B-55" });
        await _store.PutSeatAsync(tripId, ListSeatAllocationsSeatStore.Seat("L1", 1, 3, "Blocked") with { BlockReason = "Operator" });
        await _store.PutSeatAsync(tripId, ListSeatAllocationsSeatStore.Seat("5D", 1, 4, "Available"));
        return tripId;
    }
}
