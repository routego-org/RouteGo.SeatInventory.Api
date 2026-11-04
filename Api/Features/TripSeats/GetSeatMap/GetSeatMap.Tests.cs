namespace Api.Features.TripSeats.GetSeatMap;

public sealed class GetSeatMapTests : IClassFixture<GetSeatMapSeatStore>
{
    private static readonly DateTimeOffset _departure = new DateTimeOffset(2026, 10, 24, 16, 30, 0, TimeSpan.Zero);

    private readonly GetSeatMapSeatStore _store;

    public GetSeatMapTests(GetSeatMapSeatStore store)
    {
        _store = store;
    }

    [Fact]
    public async Task GetSeatMap_ScheduledTrip_ListsSeatsWithPositionClassFareAndStatus()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        await _store.PutTripAsync(tripId, _departure, false);
        await _store.PutSeatAsync(tripId, GetSeatMapSeatStore.Seat("L1", "Lower", 1, 1, "Lower berth", 1100, "Available"));
        await _store.PutSeatAsync(tripId, GetSeatMapSeatStore.HeldSeat("U1", "Upper", 1, 1, "Upper berth", 900, "B-77"));

        // Act
        GetSeatMapResponse seatMap = await _store.GetSeatMapAsync(tripId);

        // Assert
        Assert.Equal(tripId, seatMap.TripId);
        Assert.Equal(_departure, seatMap.DepartureAt);
        Assert.False(seatMap.IsCancelled);
        Assert.False(seatMap.IsDeparted);
        Assert.Equal(2, seatMap.Seats.Count);
        MapSeat lower = seatMap.Seats[0];
        Assert.Equal("L1", lower.SeatNo);
        Assert.Equal(Deck.Lower, lower.Deck);
        Assert.Equal(1, lower.Row);
        Assert.Equal(1, lower.Column);
        Assert.Equal("Lower berth", lower.SeatClass);
        Assert.Equal(1100, lower.Fare);
        Assert.Equal(SeatStatus.Available, lower.Status);
        MapSeat upper = seatMap.Seats[1];
        Assert.Equal("U1", upper.SeatNo);
        Assert.Equal(Deck.Upper, upper.Deck);
        Assert.Equal("Upper berth", upper.SeatClass);
        Assert.Equal(900, upper.Fare);
        Assert.Equal(SeatStatus.Held, upper.Status);
    }

    [Fact]
    public async Task GetSeatMap_SeatsStoredOutOfOrder_ListedByDeckThenRowThenColumn()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        await _store.PutTripAsync(tripId, _departure, false);
        await _store.PutSeatAsync(tripId, GetSeatMapSeatStore.Seat("U1", "Upper", 1, 1, "Upper berth", 900, "Available"));
        await _store.PutSeatAsync(tripId, GetSeatMapSeatStore.Seat("L3", "Lower", 2, 1, "Lower berth", 1100, "Available"));
        await _store.PutSeatAsync(tripId, GetSeatMapSeatStore.Seat("L2", "Lower", 1, 2, "Lower berth", 1100, "Available"));
        await _store.PutSeatAsync(tripId, GetSeatMapSeatStore.Seat("L1", "Lower", 1, 1, "Lower berth", 1100, "Available"));

        // Act
        GetSeatMapResponse seatMap = await _store.GetSeatMapAsync(tripId);

        // Assert
        string[] seatNos = seatMap.Seats.Select(seat => seat.SeatNo).ToArray();
        Assert.Equal(new[] { "L1", "L2", "L3", "U1" }, seatNos);
    }

    [Fact]
    public async Task GetSeatMap_SeatsHeldAndBooked_NoBookingOrHoldExpiryInAnswer()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        await _store.PutTripAsync(tripId, _departure, false);
        await _store.PutSeatAsync(tripId, GetSeatMapSeatStore.HeldSeat("U1", "Upper", 1, 1, "Upper berth", 900, "B-77"));
        await _store.PutSeatAsync(tripId, GetSeatMapSeatStore.BookedSeat("U2", "Upper", 1, 2, "Upper berth", 900, "B-55"));

        // Act
        string body = await _store.GetRawSeatMapAsync(tripId.ToString());

        // Assert
        Assert.DoesNotContain("B-77", body, StringComparison.Ordinal);
        Assert.DoesNotContain("B-55", body, StringComparison.Ordinal);
        Assert.DoesNotContain("booking", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expires", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetSeatMap_SeatInEachStatus_ShowsAvailableHeldBookedAndBlocked()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        await _store.PutTripAsync(tripId, _departure, false);
        await _store.PutSeatAsync(tripId, GetSeatMapSeatStore.Seat("1A", "Lower", 1, 1, "Seater", 700, "Available"));
        await _store.PutSeatAsync(tripId, GetSeatMapSeatStore.HeldSeat("1B", "Lower", 1, 2, "Seater", 700, "B-1"));
        await _store.PutSeatAsync(tripId, GetSeatMapSeatStore.BookedSeat("1C", "Lower", 1, 3, "Seater", 700, "B-2"));
        await _store.PutSeatAsync(tripId, GetSeatMapSeatStore.BlockedSeat("1D", "Lower", 1, 4, "Operator"));
        await _store.PutSeatAsync(tripId, GetSeatMapSeatStore.BlockedSeat("1E", "Lower", 1, 5, "TripCancelled"));

        // Act
        GetSeatMapResponse seatMap = await _store.GetSeatMapAsync(tripId);

        // Assert
        SeatStatus[] statuses = seatMap.Seats.Select(seat => seat.Status).ToArray();
        Assert.Equal(
            new[] { SeatStatus.Available, SeatStatus.Held, SeatStatus.Booked, SeatStatus.Blocked, SeatStatus.Blocked },
            statuses);
    }

    [Fact]
    public async Task GetSeatMap_FareChangedSinceCreation_ShowsTodaysFare()
    {
        // Arrange: the fare change has already been applied to the stored seat, as the worker does.
        var tripId = Guid.NewGuid();
        await _store.PutTripAsync(tripId, _departure, false);
        await _store.PutSeatAsync(tripId, GetSeatMapSeatStore.Seat("L1", "Lower", 1, 1, "Lower berth", 900, "Available"));
        await _store.PutSeatAsync(tripId, GetSeatMapSeatStore.Seat("L1", "Lower", 1, 1, "Lower berth", 1100, "Available"));

        // Act
        GetSeatMapResponse seatMap = await _store.GetSeatMapAsync(tripId);

        // Assert
        MapSeat seat = Assert.Single(seatMap.Seats);
        Assert.Equal(1100, seat.Fare);
    }

    [Fact]
    public async Task GetSeatMap_CancelledTrip_ListsSeatsAndReportsCancelled()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        await _store.PutTripAsync(tripId, _departure, true);
        await _store.PutSeatAsync(tripId, GetSeatMapSeatStore.BlockedSeat("L1", "Lower", 1, 1, "TripCancelled"));
        await _store.PutSeatAsync(tripId, GetSeatMapSeatStore.BlockedSeat("L2", "Lower", 1, 2, "TripCancelled"));

        // Act
        GetSeatMapResponse seatMap = await _store.GetSeatMapAsync(tripId);

        // Assert
        Assert.True(seatMap.IsCancelled);
        Assert.Equal(2, seatMap.Seats.Count);
        Assert.All(seatMap.Seats, seat => Assert.Equal(SeatStatus.Blocked, seat.Status));
    }

    [Fact]
    public async Task GetSeatMap_DepartureTimePassed_ListsSeatsAndReportsDeparted()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        DateTimeOffset departedAt = GetSeatMapSeatStore.Now.AddHours(-2);
        await _store.PutTripAsync(tripId, departedAt, false);
        await _store.PutSeatAsync(tripId, GetSeatMapSeatStore.BookedSeat("L1", "Lower", 1, 1, "Lower berth", 1100, "B-9"));

        // Act
        GetSeatMapResponse seatMap = await _store.GetSeatMapAsync(tripId);

        // Assert
        Assert.True(seatMap.IsDeparted);
        Assert.False(seatMap.IsCancelled);
        MapSeat seat = Assert.Single(seatMap.Seats);
        Assert.Equal(SeatStatus.Booked, seat.Status);
    }

    [Fact]
    public async Task GetSeatMap_UnknownTrip_RefusedNotFound()
    {
        // Arrange
        var tripId = Guid.NewGuid();

        // Act
        GetSeatMapProblem problem = await _store.GetProblemAsync(tripId.ToString());

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, problem.Status);
        Assert.Equal("NOT_FOUND", problem.Code);
        Assert.Equal("urn:routego:seat-inventory:problems:not-found", problem.Type);
    }

    [Fact]
    public async Task GetSeatMap_TripKnownOnlyFromEarlyCancellation_RefusedNotFound()
    {
        // Arrange: a cancellation that arrived before the trip was scheduled — no departure, no seats.
        var tripId = Guid.NewGuid();
        await _store.PutCancelledOnlyTripAsync(tripId);

        // Act
        GetSeatMapProblem problem = await _store.GetProblemAsync(tripId.ToString());

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, problem.Status);
        Assert.Equal("NOT_FOUND", problem.Code);
    }

    [Fact]
    public async Task GetSeatMap_TripIdNotAUuid_RefusedValidationFailed()
    {
        // Act
        GetSeatMapProblem problem = await _store.GetProblemAsync("not-a-trip");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, problem.Status);
        Assert.Equal("VALIDATION_FAILED", problem.Code);
        Assert.Equal("urn:routego:seat-inventory:problems:validation-failed", problem.Type);
        Assert.Contains("tripId", problem.ErrorFields);
    }

    [Fact]
    public async Task GetSeatMap_SixtySeatTripAskedHundredTimes_AtLeastNinetyFiveWithinHalfASecond()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        await _store.PutTripAsync(tripId, _departure, false);
        await _store.PutSeatsAsync(tripId, 60);

        // Act
        int fastAnswers = await _store.CountAnswersWithinAsync(tripId, 100, TimeSpan.FromMilliseconds(500));

        // Assert
        Assert.True(fastAnswers >= 95, $"Only {fastAnswers} of 100 seat maps answered within 500 ms.");
    }
}

/// <summary>A refused seat map request, read from its RFC 7807 body.</summary>
public sealed record GetSeatMapProblem
{
    public required HttpStatusCode Status { get; init; }

    public required string? Code { get; init; }

    public required string? Type { get; init; }

    public required IReadOnlyList<string> ErrorFields { get; init; }
}

/// <summary>A seat as the trip-seat-creation worker stores it.</summary>
public sealed record GetSeatMapStoredSeat
{
    public required string SeatNo { get; init; }

    public required string Deck { get; init; }

    public required int Row { get; init; }

    public required int Column { get; init; }

    public required string SeatClass { get; init; }

    public required int Fare { get; init; }

    public required string Status { get; init; }

    public string? BookingId { get; init; }

    public string? HoldExpiresAt { get; init; }

    public string? BlockReason { get; init; }
}

/// <summary>A clock that always reads the same instant, so departed-or-not never depends on today's date.</summary>
public sealed class GetSeatMapFixedClock : IClock
{
    private readonly DateTimeOffset _utcNow;

    public GetSeatMapFixedClock(DateTimeOffset utcNow)
    {
        _utcNow = utcNow;
    }

    public DateTimeOffset UtcNow
    {
        get
        {
            return _utcNow;
        }
    }
}

/// <summary>
/// DynamoDB Local in Docker with the seat store table, and the API host pointed at it. Shared by
/// every test in the class; each test uses its own trip, so no test sees another's data.
/// </summary>
public sealed class GetSeatMapSeatStore : IAsyncLifetime
{
    /// <summary>The instant the API believes it is — before the default departure in the tests.</summary>
    public static readonly DateTimeOffset Now = new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);

    private const string TableName = "seat-store-tests";
    private const int BatchWriteLimit = 25;

    private readonly DynamoDbContainer _container = new DynamoDbBuilder("amazon/dynamodb-local:2.6.1").Build();
    private AmazonDynamoDBClient? _dynamoDb;
    private ApiFactory? _factory;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        string serviceUrl = _container.GetConnectionString();

        // The host reads these at startup, exactly as a deployed function reads its environment.
        // They are process-wide and other slices' test classes run alongside this one, so every
        // class sets the same values; each points the host at its own container by replacing the
        // DynamoDB client instead.
        Environment.SetEnvironmentVariable("SeatStoreTableName", TableName);
        Environment.SetEnvironmentVariable("DynamoDbServiceUrl", "http://localhost:1");
        Environment.SetEnvironmentVariable("AWS_REGION", "ap-south-1");
        Environment.SetEnvironmentVariable("IdentityIssuer", "http://localhost:5001");
        Environment.SetEnvironmentVariable("IdentityAudience", "routego");
        Environment.SetEnvironmentVariable("IdentityJwksUrl", "http://localhost:5001/.well-known/jwks.json");
        Environment.SetEnvironmentVariable("HoldExpiryQueueUrl", "http://localhost:1/000000000000/hold-expiry-tests");
        Environment.SetEnvironmentVariable("SqsServiceUrl", "http://localhost:1");
        Environment.SetEnvironmentVariable("routego/internal-key", "seat-inventory-tests-internal-key");

        var config = new AmazonDynamoDBConfig { ServiceURL = serviceUrl };
        _dynamoDb = new AmazonDynamoDBClient(new Amazon.Runtime.BasicAWSCredentials("local", "local"), config);
        await CreateTableAsync(_dynamoDb);

        var fixedClock = new Microsoft.Extensions.DependencyInjection.ServiceDescriptor(
            typeof(IClock), new GetSeatMapFixedClock(Now));
        var seatStore = new Microsoft.Extensions.DependencyInjection.ServiceDescriptor(
            typeof(IAmazonDynamoDB), _dynamoDb);
        _factory = new ApiFactory(services => services.Add(fixedClock), services => services.Add(seatStore));
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory != null)
        {
            await _factory.DisposeAsync();
        }

        _dynamoDb?.Dispose();
        await _container.DisposeAsync();
    }

    public static GetSeatMapStoredSeat Seat(
        string seatNo, string deck, int row, int column, string seatClass, int fare, string status)
    {
        return new GetSeatMapStoredSeat
        {
            SeatNo = seatNo,
            Deck = deck,
            Row = row,
            Column = column,
            SeatClass = seatClass,
            Fare = fare,
            Status = status,
        };
    }

    public static GetSeatMapStoredSeat HeldSeat(
        string seatNo, string deck, int row, int column, string seatClass, int fare, string bookingId)
    {
        GetSeatMapStoredSeat seat = Seat(seatNo, deck, row, column, seatClass, fare, "Held");
        return seat with { BookingId = bookingId, HoldExpiresAt = Now.AddMinutes(10).ToString("O") };
    }

    public static GetSeatMapStoredSeat BookedSeat(
        string seatNo, string deck, int row, int column, string seatClass, int fare, string bookingId)
    {
        GetSeatMapStoredSeat seat = Seat(seatNo, deck, row, column, seatClass, fare, "Booked");
        return seat with { BookingId = bookingId };
    }

    public static GetSeatMapStoredSeat BlockedSeat(string seatNo, string deck, int row, int column, string blockReason)
    {
        GetSeatMapStoredSeat seat = Seat(seatNo, deck, row, column, "Seater", 700, "Blocked");
        return seat with { BlockReason = blockReason };
    }

    public async Task PutTripAsync(Guid tripId, DateTimeOffset departureAt, bool isCancelled)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            { "pk", new AttributeValue { S = TripKey(tripId) } },
            { "sk", new AttributeValue { S = "TRIP" } },
            { "operatorId", new AttributeValue { S = "OP-1" } },
            { "departureAt", new AttributeValue { S = departureAt.UtcDateTime.ToString("O") } },
            { "isCancelled", new AttributeValue { BOOL = isCancelled } },
        };
        await _dynamoDb!.PutItemAsync(new PutItemRequest { TableName = TableName, Item = item });
    }

    public async Task PutCancelledOnlyTripAsync(Guid tripId)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            { "pk", new AttributeValue { S = TripKey(tripId) } },
            { "sk", new AttributeValue { S = "TRIP" } },
            { "isCancelled", new AttributeValue { BOOL = true } },
        };
        await _dynamoDb!.PutItemAsync(new PutItemRequest { TableName = TableName, Item = item });
    }

    public async Task PutSeatAsync(Guid tripId, GetSeatMapStoredSeat seat)
    {
        Dictionary<string, AttributeValue> item = ToItem(tripId, seat);
        await _dynamoDb!.PutItemAsync(new PutItemRequest { TableName = TableName, Item = item });
    }

    public async Task PutSeatsAsync(Guid tripId, int seatCount)
    {
        var requests = new List<WriteRequest>();
        for (int i = 1; i <= seatCount; i++)
        {
            GetSeatMapStoredSeat seat = Seat($"S{i}", "Lower", ((i - 1) / 4) + 1, ((i - 1) % 4) + 1, "Seater", 700, "Available");
            requests.Add(new WriteRequest { PutRequest = new PutRequest { Item = ToItem(tripId, seat) } });
        }

        foreach (WriteRequest[] batch in requests.Chunk(BatchWriteLimit))
        {
            var write = new BatchWriteItemRequest
            {
                RequestItems = new Dictionary<string, List<WriteRequest>> { { TableName, batch.ToList() } },
            };
            await _dynamoDb!.BatchWriteItemAsync(write);
        }
    }

    public async Task<GetSeatMapResponse> GetSeatMapAsync(Guid tripId)
    {
        HttpResponseMessage response = await _client!.GetAsync($"/trips/{tripId}/seats");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        GetSeatMapResponse? seatMap = await response.Content.ReadFromJsonAsync<GetSeatMapResponse>(WireJson.Options);
        return seatMap!;
    }

    public async Task<string> GetRawSeatMapAsync(string tripId)
    {
        HttpResponseMessage response = await _client!.GetAsync($"/trips/{tripId}/seats");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    public async Task<GetSeatMapProblem> GetProblemAsync(string tripId)
    {
        HttpResponseMessage response = await _client!.GetAsync($"/trips/{tripId}/seats");
        string body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;

        var errorFields = new List<string>();
        if (root.TryGetProperty("errors", out JsonElement errors))
        {
            foreach (JsonProperty field in errors.EnumerateObject())
            {
                errorFields.Add(field.Name);
            }
        }

        return new GetSeatMapProblem
        {
            Status = response.StatusCode,
            Code = ReadString(root, "code"),
            Type = ReadString(root, "type"),
            ErrorFields = errorFields,
        };
    }

    public async Task<int> CountAnswersWithinAsync(Guid tripId, int requestCount, TimeSpan limit)
    {
        // One request at a time: the criterion is about how long each answer takes, and concurrent
        // requests would measure queueing on the test machine instead.
        int fastAnswers = 0;
        for (int i = 0; i < requestCount; i++)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            HttpResponseMessage response = await _client!.GetAsync($"/trips/{tripId}/seats");
            await response.Content.ReadAsByteArrayAsync();
            stopwatch.Stop();
            if (response.StatusCode == HttpStatusCode.OK && stopwatch.Elapsed <= limit)
            {
                fastAnswers++;
            }
        }

        return fastAnswers;
    }

    private static string TripKey(Guid tripId)
    {
        return "TRIP#" + tripId.ToString("D");
    }

    private static string? ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        return value.GetString();
    }

    private static Dictionary<string, AttributeValue> ToItem(Guid tripId, GetSeatMapStoredSeat seat)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            { "pk", new AttributeValue { S = TripKey(tripId) } },
            { "sk", new AttributeValue { S = "SEAT#" + seat.SeatNo } },
            { "seatNo", new AttributeValue { S = seat.SeatNo } },
            { "deck", new AttributeValue { S = seat.Deck } },
            { "row", new AttributeValue { N = seat.Row.ToString(System.Globalization.CultureInfo.InvariantCulture) } },
            { "column", new AttributeValue { N = seat.Column.ToString(System.Globalization.CultureInfo.InvariantCulture) } },
            { "seatClass", new AttributeValue { S = seat.SeatClass } },
            { "fare", new AttributeValue { N = seat.Fare.ToString(System.Globalization.CultureInfo.InvariantCulture) } },
            { "status", new AttributeValue { S = seat.Status } },
        };
        if (seat.BookingId != null)
        {
            item["bookingId"] = new AttributeValue { S = seat.BookingId };
        }

        if (seat.HoldExpiresAt != null)
        {
            item["holdExpiresAt"] = new AttributeValue { S = seat.HoldExpiresAt };
        }

        if (seat.BlockReason != null)
        {
            item["blockReason"] = new AttributeValue { S = seat.BlockReason };
        }

        return item;
    }

    private static async Task CreateTableAsync(AmazonDynamoDBClient dynamoDb)
    {
        var request = new CreateTableRequest
        {
            TableName = TableName,
            BillingMode = BillingMode.PAY_PER_REQUEST,
            AttributeDefinitions = new List<AttributeDefinition>
            {
                new AttributeDefinition { AttributeName = "pk", AttributeType = ScalarAttributeType.S },
                new AttributeDefinition { AttributeName = "sk", AttributeType = ScalarAttributeType.S },
            },
            KeySchema = new List<KeySchemaElement>
            {
                new KeySchemaElement { AttributeName = "pk", KeyType = KeyType.HASH },
                new KeySchemaElement { AttributeName = "sk", KeyType = KeyType.RANGE },
            },
        };
        await dynamoDb.CreateTableAsync(request);
    }
}
