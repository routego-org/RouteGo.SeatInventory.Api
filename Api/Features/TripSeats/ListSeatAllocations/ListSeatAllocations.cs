namespace Api.Features.TripSeats.ListSeatAllocations;

/// <summary>The trip whose seat allocations are asked for.</summary>
/// <param name="TripId">The trip identifier as it arrived in the route; must be a UUID.</param>
public sealed record ListSeatAllocationsRequest(string? TripId);

/// <summary>Who is asking: an admin may see any trip, an operator only its own.</summary>
/// <param name="IsAdmin">True for the Admin role.</param>
/// <param name="OperatorId">The operatorId claim of an Operator token; null for an admin.</param>
public sealed record SeatAllocationsCaller(bool IsAdmin, string? OperatorId);

/// <summary>One seat as staff see it: the public view plus who holds or owns it and why it is blocked.</summary>
public sealed record SeatAllocation
{
    /// <summary>The seat label, unique within the trip.</summary>
    public required string SeatNo { get; init; }

    /// <summary>The deck the seat is on.</summary>
    public required Deck Deck { get; init; }

    /// <summary>The seat's row on its deck, from 1.</summary>
    public required int Row { get; init; }

    /// <summary>The seat's column on its deck, from 1.</summary>
    public required int Column { get; init; }

    /// <summary>The operator's seat class name.</summary>
    public required string SeatClass { get; init; }

    /// <summary>Today's fare in whole Indian rupees (INR).</summary>
    public required int Fare { get; init; }

    /// <summary>Whether the seat can be sold right now.</summary>
    public required SeatStatus Status { get; init; }

    /// <summary>The booking that holds (held) or owns (booked) the seat; null otherwise.</summary>
    public required string? BookingId { get; init; }

    /// <summary>When the hold ends unless confirmed; held seats only. May be in the past when the hold timer has not run yet.</summary>
    public required DateTimeOffset? HoldExpiresAt { get; init; }

    /// <summary>Why the seat is blocked; blocked seats only.</summary>
    public required BlockReason? BlockReason { get; init; }
}

/// <summary>Every seat of a trip, in the seat map's order, with its holder or owner.</summary>
public sealed record ListSeatAllocationsResponse
{
    /// <summary>The trip identifier.</summary>
    public required Guid TripId { get; init; }

    /// <summary>The trip's operator.</summary>
    public required Guid OperatorId { get; init; }

    /// <summary>When the trip departs (UTC).</summary>
    public required DateTimeOffset DepartureAt { get; init; }

    /// <summary>True when Fleet cancelled the trip.</summary>
    public required bool IsCancelled { get; init; }

    /// <summary>True when the departure time has passed, judged at the time of the request.</summary>
    public required bool IsDeparted { get; init; }

    /// <summary>Every seat of the trip.</summary>
    public required IReadOnlyList<SeatAllocation> Items { get; init; }
}

/// <summary>How a seat allocations request ended.</summary>
public enum ListSeatAllocationsOutcome
{
    Found,
    TripNotFound,
    NotTheTripsOperator,
}

/// <summary>The outcome, plus the allocations when the caller may see them.</summary>
public sealed record ListSeatAllocationsResult(
    ListSeatAllocationsOutcome Outcome, ListSeatAllocationsResponse? Response = null);

/// <summary>Checks that the trip identifier is a UUID before the seat store is asked.</summary>
public sealed class ListSeatAllocationsValidator : AbstractValidator<ListSeatAllocationsRequest>
{
    public ListSeatAllocationsValidator()
    {
        RuleFor(x => x.TripId)
            .Must(BeATripIdentifier)
            .WithMessage("tripId must be a trip identifier (a UUID).");
    }

    private static bool BeATripIdentifier(string? tripId)
    {
        return Guid.TryParse(tripId, out _);
    }
}

/// <summary>Reads a trip and all its seats from the seat store, for an admin or the trip's operator.</summary>
public sealed class ListSeatAllocationsHandler
{
    // A trip and its seats share one partition, so one query reads every allocation.
    private const string TripKeyPrefix = "TRIP#";
    private const string TripSortKey = "TRIP";

    // ROW, COLUMN and STATUS are DynamoDB reserved words, hence the placeholders.
    private const string Projection =
        "sk, operatorId, departureAt, isCancelled, seatNo, deck, #row, #column, seatClass, fare, #status, "
        + "bookingId, holdExpiresAt, blockReason";

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;
    private readonly IClock _clock;

    public ListSeatAllocationsHandler(IAmazonDynamoDB dynamoDb, string tableName, IClock clock)
    {
        _dynamoDb = dynamoDb;
        _tableName = tableName;
        _clock = clock;
    }

    public async Task<ListSeatAllocationsResult> HandleAsync(
        ListSeatAllocationsRequest request, SeatAllocationsCaller caller, CancellationToken cancellationToken)
    {
        var tripId = Guid.Parse(request.TripId!);

        List<Dictionary<string, AttributeValue>> items = await ListTripItemsAsync(tripId, cancellationToken);

        Dictionary<string, AttributeValue>? trip = items.Find(IsTripItem);
        // A trip known only from an early cancellation has no operator, departure time or seats
        // yet: Fleet has not announced it, and the contract answers an unannounced trip as not found.
        if (trip is null || !trip.ContainsKey("departureAt"))
        {
            return new ListSeatAllocationsResult(ListSeatAllocationsOutcome.TripNotFound);
        }

        // The trip has to be read before ownership can be judged, because the contract tells an
        // operator naming someone else's trip FORBIDDEN, not "not found".
        string tripOperatorId = trip["operatorId"].S;
        if (!caller.IsAdmin && !string.Equals(tripOperatorId, caller.OperatorId, StringComparison.OrdinalIgnoreCase))
        {
            return new ListSeatAllocationsResult(ListSeatAllocationsOutcome.NotTheTripsOperator);
        }

        List<SeatAllocation> seats = ToSeatAllocations(items);
        seats.Sort(CompareByPosition);
        ListSeatAllocationsResponse response = ToResponse(tripId, trip, seats, _clock.UtcNow);
        return new ListSeatAllocationsResult(ListSeatAllocationsOutcome.Found, response);
    }

    private async Task<List<Dictionary<string, AttributeValue>>> ListTripItemsAsync(
        Guid tripId, CancellationToken cancellationToken)
    {
        var query = new QueryRequest
        {
            TableName = _tableName,
            KeyConditionExpression = "pk = :pk",
            ProjectionExpression = Projection,
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#row", "row" },
                { "#column", "column" },
                { "#status", "status" },
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":pk", new AttributeValue { S = TripKeyPrefix + tripId.ToString("D") } },
            },
        };

        // One query page holds up to 1 MB, far more than a bus; the loop only guards against a
        // trip ever outgrowing a page, so a seat is never silently missing from the list.
        var items = new List<Dictionary<string, AttributeValue>>();
        do
        {
            QueryResponse page = await _dynamoDb.QueryAsync(query, cancellationToken);
            if (page.Items != null)
            {
                items.AddRange(page.Items);
            }

            query.ExclusiveStartKey = page.LastEvaluatedKey;
        }
        while (query.ExclusiveStartKey != null && query.ExclusiveStartKey.Count > 0);

        return items;
    }

    private static bool IsTripItem(Dictionary<string, AttributeValue> item)
    {
        return item["sk"].S == TripSortKey;
    }

    private static List<SeatAllocation> ToSeatAllocations(List<Dictionary<string, AttributeValue>> items)
    {
        var seats = new List<SeatAllocation>();
        foreach (Dictionary<string, AttributeValue> item in items)
        {
            if (IsTripItem(item))
            {
                continue;
            }

            seats.Add(ToSeatAllocation(item));
        }

        return seats;
    }

    private static SeatAllocation ToSeatAllocation(Dictionary<string, AttributeValue> item)
    {
        return new SeatAllocation
        {
            SeatNo = item["seatNo"].S,
            Deck = Enum.Parse<Deck>(item["deck"].S),
            Row = int.Parse(item["row"].N, System.Globalization.CultureInfo.InvariantCulture),
            Column = int.Parse(item["column"].N, System.Globalization.CultureInfo.InvariantCulture),
            SeatClass = item["seatClass"].S,
            Fare = int.Parse(item["fare"].N, System.Globalization.CultureInfo.InvariantCulture),
            Status = Enum.Parse<SeatStatus>(item["status"].S),
            BookingId = ReadOptionalString(item, "bookingId"),
            HoldExpiresAt = ReadOptionalTime(item, "holdExpiresAt"),
            BlockReason = ReadOptionalBlockReason(item),
        };
    }

    private static string? ReadOptionalString(Dictionary<string, AttributeValue> item, string name)
    {
        if (!item.TryGetValue(name, out AttributeValue? value))
        {
            return null;
        }

        return value.S;
    }

    private static DateTimeOffset? ReadOptionalTime(Dictionary<string, AttributeValue> item, string name)
    {
        string? text = ReadOptionalString(item, name);
        if (text is null)
        {
            return null;
        }

        return ParseUtc(text);
    }

    private static BlockReason? ReadOptionalBlockReason(Dictionary<string, AttributeValue> item)
    {
        string? text = ReadOptionalString(item, "blockReason");
        if (text is null)
        {
            return null;
        }

        return Enum.Parse<BlockReason>(text);
    }

    private static DateTimeOffset ParseUtc(string text)
    {
        var parsed = DateTimeOffset.Parse(
            text,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal);
        return parsed.ToUniversalTime();
    }

    private static int CompareByPosition(SeatAllocation left, SeatAllocation right)
    {
        int byDeck = left.Deck.CompareTo(right.Deck);
        if (byDeck != 0)
        {
            return byDeck;
        }

        int byRow = left.Row.CompareTo(right.Row);
        if (byRow != 0)
        {
            return byRow;
        }

        return left.Column.CompareTo(right.Column);
    }

    private static ListSeatAllocationsResponse ToResponse(
        Guid tripId, Dictionary<string, AttributeValue> trip, List<SeatAllocation> seats, DateTimeOffset now)
    {
        DateTimeOffset departureAt = ParseUtc(trip["departureAt"].S);
        bool isCancelled = trip.ContainsKey("isCancelled") && trip["isCancelled"].BOOL == true;

        return new ListSeatAllocationsResponse
        {
            TripId = tripId,
            OperatorId = Guid.Parse(trip["operatorId"].S),
            DepartureAt = departureAt,
            IsCancelled = isCancelled,
            IsDeparted = departureAt <= now,
            Items = seats,
        };
    }
}

[ApiController]
[ApiExplorerSettings(GroupName = "TripSeats")]
[Route("trips/{tripId}/seatAllocations")]
public sealed class ListSeatAllocationsController : ControllerBase
{
    private readonly ListSeatAllocationsHandler _handler;
    private readonly ListSeatAllocationsValidator _validator = new();

    public ListSeatAllocationsController(IAmazonDynamoDB dynamoDb, IConfiguration configuration, IClock clock)
    {
        // Present by the time any request arrives: startup refuses to run without it.
        string tableName = configuration[Infrastructure.ConfigKeys.SeatStoreTableName]!;
        _handler = new ListSeatAllocationsHandler(dynamoDb, tableName, clock);
    }

    /// <summary>Show every seat of a trip with its holder or owner, hold expiry and block reason (Admin; the trip's Operator).</summary>
    /// <param name="tripId">The trip identifier (UUID).</param>
    /// <param name="cancellationToken">Cancelled when the caller goes away.</param>
    /// <response code="200">The trip's seat allocations.</response>
    /// <response code="400">The trip identifier is not a UUID (VALIDATION_FAILED).</response>
    /// <response code="401">No valid access token was presented (UNAUTHORIZED).</response>
    /// <response code="403">The caller is neither an admin nor the trip's operator (FORBIDDEN).</response>
    /// <response code="404">No trip with this identifier is known (NOT_FOUND).</response>
    [Authorize(Roles = AccessRoles.AdminOrOperator)]
    [HttpGet]
    [ProducesResponseType(typeof(ListSeatAllocationsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListSeatAllocations(string tripId, CancellationToken cancellationToken)
    {
        var request = new ListSeatAllocationsRequest(tripId);
        ValidationResult validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ProblemResult.ValidationFailed(this, validation.ToDictionary());
        }

        string? operatorId = User.FindFirst(TokenClaims.OperatorId)?.Value;
        var caller = new SeatAllocationsCaller(User.IsInRole(AccessRoles.Admin), operatorId);

        ListSeatAllocationsResult result = await _handler.HandleAsync(request, caller, cancellationToken);
        switch (result.Outcome)
        {
            case ListSeatAllocationsOutcome.Found:
                return Ok(result.Response);
            case ListSeatAllocationsOutcome.TripNotFound:
                return ProblemResult.NotFound(this, $"No trip with identifier '{tripId}' is known.");
            case ListSeatAllocationsOutcome.NotTheTripsOperator:
                return ProblemResult.Forbidden(this, $"Trip '{tripId}' belongs to another operator.");
            default:
                throw new InvalidOperationException($"Unhandled outcome '{result.Outcome}'.");
        }
    }
}
