namespace Api.Features.TripSeats.GetSeatMap;

/// <summary>The trip whose seat map is asked for.</summary>
/// <param name="TripId">The trip identifier as it arrived in the route; must be a UUID.</param>
public sealed record GetSeatMapRequest(string? TripId);

/// <summary>One seat as the public seat map shows it.</summary>
public sealed record MapSeat
{
    /// <summary>The seat label, unique within the trip (for example 1A, U4).</summary>
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
}

/// <summary>A trip's seat map: every seat, listed deck by deck, then by row and column.</summary>
public sealed record GetSeatMapResponse
{
    /// <summary>The trip identifier.</summary>
    public required Guid TripId { get; init; }

    /// <summary>When the trip departs (UTC).</summary>
    public required DateTimeOffset DepartureAt { get; init; }

    /// <summary>True when Fleet cancelled the trip.</summary>
    public required bool IsCancelled { get; init; }

    /// <summary>True when the departure time has passed, judged at the time of the request.</summary>
    public required bool IsDeparted { get; init; }

    /// <summary>Every seat of the trip.</summary>
    public required IReadOnlyList<MapSeat> Seats { get; init; }
}

/// <summary>How a seat map request ended.</summary>
public enum GetSeatMapOutcome
{
    Found,
    TripNotFound,
}

/// <summary>The outcome, plus the seat map when one was found.</summary>
public sealed record GetSeatMapResult(GetSeatMapOutcome Outcome, GetSeatMapResponse? Response = null);

/// <summary>Checks that the trip identifier is a UUID before the seat store is asked.</summary>
public sealed class GetSeatMapValidator : AbstractValidator<GetSeatMapRequest>
{
    public GetSeatMapValidator()
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

/// <summary>Reads a trip and all its seats from the seat store and builds the public seat map.</summary>
public sealed class GetSeatMapHandler
{
    // A trip and its seats share one partition, so one query reads the whole seat map.
    private const string TripKeyPrefix = "TRIP#";
    private const string TripSortKey = "TRIP";

    // Only what the public map shows is read: booking identifiers and hold expiry stay in the store.
    // ROW, COLUMN and STATUS are DynamoDB reserved words, hence the placeholders.
    private const string Projection =
        "sk, departureAt, isCancelled, seatNo, deck, #row, #column, seatClass, fare, #status";

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;
    private readonly IClock _clock;

    public GetSeatMapHandler(IAmazonDynamoDB dynamoDb, string tableName, IClock clock)
    {
        _dynamoDb = dynamoDb;
        _tableName = tableName;
        _clock = clock;
    }

    public async Task<GetSeatMapResult> HandleAsync(GetSeatMapRequest request, CancellationToken cancellationToken)
    {
        var tripId = Guid.Parse(request.TripId!);

        List<Dictionary<string, AttributeValue>> items = await ListTripItemsAsync(tripId, cancellationToken);

        Dictionary<string, AttributeValue>? trip = items.Find(IsTripItem);
        // A trip known only from an early cancellation has no departure time and no seats yet: Fleet
        // has not announced it, and the contract answers an unannounced trip as not found.
        if (trip is null || !trip.ContainsKey("departureAt"))
        {
            return new GetSeatMapResult(GetSeatMapOutcome.TripNotFound);
        }

        List<MapSeat> seats = ToMapSeats(items);
        seats.Sort(CompareByPosition);
        GetSeatMapResponse response = ToResponse(tripId, trip, seats, _clock.UtcNow);
        return new GetSeatMapResult(GetSeatMapOutcome.Found, response);
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
        // trip ever outgrowing a page, so a seat is never silently missing from the map.
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

    private static List<MapSeat> ToMapSeats(List<Dictionary<string, AttributeValue>> items)
    {
        var seats = new List<MapSeat>();
        foreach (Dictionary<string, AttributeValue> item in items)
        {
            if (IsTripItem(item))
            {
                continue;
            }

            seats.Add(ToMapSeat(item));
        }

        return seats;
    }

    private static MapSeat ToMapSeat(Dictionary<string, AttributeValue> item)
    {
        return new MapSeat
        {
            SeatNo = item["seatNo"].S,
            Deck = Enum.Parse<Deck>(item["deck"].S),
            Row = int.Parse(item["row"].N, System.Globalization.CultureInfo.InvariantCulture),
            Column = int.Parse(item["column"].N, System.Globalization.CultureInfo.InvariantCulture),
            SeatClass = item["seatClass"].S,
            Fare = int.Parse(item["fare"].N, System.Globalization.CultureInfo.InvariantCulture),
            Status = Enum.Parse<SeatStatus>(item["status"].S),
        };
    }

    private static int CompareByPosition(MapSeat left, MapSeat right)
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

    private static GetSeatMapResponse ToResponse(
        Guid tripId, Dictionary<string, AttributeValue> trip, List<MapSeat> seats, DateTimeOffset now)
    {
        var departureAt = DateTimeOffset.Parse(
            trip["departureAt"].S,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal);
        bool isCancelled = trip.ContainsKey("isCancelled") && trip["isCancelled"].BOOL == true;

        return new GetSeatMapResponse
        {
            TripId = tripId,
            DepartureAt = departureAt.ToUniversalTime(),
            IsCancelled = isCancelled,
            IsDeparted = departureAt <= now,
            Seats = seats,
        };
    }
}

[ApiController]
[ApiExplorerSettings(GroupName = "TripSeats")]
[Route("trips/{tripId}/seats")]
public sealed class GetSeatMapController : ControllerBase
{
    private readonly GetSeatMapHandler _handler;
    private readonly GetSeatMapValidator _validator = new();

    public GetSeatMapController(IAmazonDynamoDB dynamoDb, IConfiguration configuration, IClock clock)
    {
        // Present by the time any request arrives: startup refuses to run without it.
        string tableName = configuration[Infrastructure.ConfigKeys.SeatStoreTableName]!;
        _handler = new GetSeatMapHandler(dynamoDb, tableName, clock);
    }

    /// <summary>Show a trip's seats, their position, class, current fare and status (public).</summary>
    /// <param name="tripId">The trip identifier (UUID).</param>
    /// <param name="cancellationToken">Cancelled when the caller goes away.</param>
    /// <response code="200">The trip's seat map.</response>
    /// <response code="400">The trip identifier is not a UUID (VALIDATION_FAILED).</response>
    /// <response code="404">No trip with this identifier is known (NOT_FOUND).</response>
    // Anonymous on purpose: anyone may see a seat map before signing in, and a token that is sent
    // changes nothing because the answer never says who holds or owns a seat.
    [AllowAnonymous]
    [HttpGet]
    [ProducesResponseType(typeof(GetSeatMapResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSeatMap(string tripId, CancellationToken cancellationToken)
    {
        var request = new GetSeatMapRequest(tripId);
        ValidationResult validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ProblemResult.ValidationFailed(this, validation.ToDictionary());
        }

        GetSeatMapResult result = await _handler.HandleAsync(request, cancellationToken);
        switch (result.Outcome)
        {
            case GetSeatMapOutcome.Found:
                return Ok(result.Response);
            case GetSeatMapOutcome.TripNotFound:
                return ProblemResult.NotFound(this, $"No trip with identifier '{tripId}' is known.");
            default:
                throw new InvalidOperationException($"Unhandled outcome '{result.Outcome}'.");
        }
    }
}
