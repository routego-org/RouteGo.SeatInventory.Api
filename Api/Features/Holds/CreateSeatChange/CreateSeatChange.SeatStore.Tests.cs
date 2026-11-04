namespace Api.Features.Holds.CreateSeatChange;

/// <summary>A refused seat change request, read from its RFC 7807 body.</summary>
public sealed record CreateSeatChangeProblem
{
    public required HttpStatusCode Status { get; init; }

    public required string? Code { get; init; }

    /// <summary>The seats the problem names, from whichever member the code carries.</summary>
    public required IReadOnlyList<string> NamedSeats { get; init; }

    /// <summary>The moves a class-mismatch problem names, each as "from>to".</summary>
    public required IReadOnlyList<string> NamedMoves { get; init; }

    public required IReadOnlyList<string> ErrorFields { get; init; }
}

/// <summary>A clock that always reads the same instant, so departed-or-not never depends on today's date.</summary>
public sealed class CreateSeatChangeFixedClock : IClock
{
    private readonly DateTimeOffset _utcNow;

    public CreateSeatChangeFixedClock(DateTimeOffset utcNow)
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
/// DynamoDB Local in Docker with the seat store table, and the API host pointed at it. Seats are
/// read back raw, because the change record on each seat is what the seat-change stream turns into
/// the SeatChanged event.
/// </summary>
public sealed class CreateSeatChangeSeatStore : IAsyncLifetime
{
    /// <summary>The instant the API believes it is.</summary>
    public static readonly DateTimeOffset Now = new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);

    /// <summary>The internal key Booking presents — the same value every slice's test class injects.</summary>
    public const string InternalKey = "seat-inventory-tests-internal-key";

    // Process-wide values every slice's test class sets identically (see GetSeatMap's fixture).
    private const string TableName = "seat-store-tests";
    private const string Issuer = "http://localhost:5001";
    private const string Audience = "routego";

    private readonly DynamoDbContainer _container = new DynamoDbBuilder("amazon/dynamodb-local:2.6.1").Build();
    private AmazonDynamoDBClient? _dynamoDb;
    private ApiFactory? _factory;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        Environment.SetEnvironmentVariable("SeatStoreTableName", TableName);
        Environment.SetEnvironmentVariable("DynamoDbServiceUrl", "http://localhost:1");
        Environment.SetEnvironmentVariable("AWS_REGION", "ap-south-1");
        Environment.SetEnvironmentVariable("IdentityIssuer", Issuer);
        Environment.SetEnvironmentVariable("IdentityAudience", Audience);
        Environment.SetEnvironmentVariable("IdentityJwksUrl", "http://localhost:5001/.well-known/jwks.json");
        Environment.SetEnvironmentVariable("HoldExpiryQueueUrl", "http://localhost:1/000000000000/hold-expiry-tests");
        Environment.SetEnvironmentVariable("SqsServiceUrl", "http://localhost:1");
        Environment.SetEnvironmentVariable("routego/internal-key", InternalKey);

        var config = new AmazonDynamoDBConfig { ServiceURL = _container.GetConnectionString() };
        _dynamoDb = new AmazonDynamoDBClient(new Amazon.Runtime.BasicAWSCredentials("local", "local"), config);
        await CreateTableAsync(_dynamoDb);

        var fixedClock = new Microsoft.Extensions.DependencyInjection.ServiceDescriptor(typeof(IClock), new CreateSeatChangeFixedClock(Now));
        var seatStore = new Microsoft.Extensions.DependencyInjection.ServiceDescriptor(typeof(IAmazonDynamoDB), _dynamoDb);
        // Booking calls with the internal key only, so no token signing key needs trusting here.
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

    public async Task PutTripAsync(Guid tripId, DateTimeOffset departureAt, bool isCancelled)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            { "pk", new AttributeValue { S = "TRIP#" + tripId.ToString("D") } },
            { "sk", new AttributeValue { S = "TRIP" } },
            { "operatorId", new AttributeValue { S = Guid.NewGuid().ToString("D") } },
            { "departureAt", new AttributeValue { S = departureAt.UtcDateTime.ToString("O") } },
            { "isCancelled", new AttributeValue { BOOL = isCancelled } },
        };
        await _dynamoDb!.PutItemAsync(new PutItemRequest { TableName = TableName, Item = item });
    }

    /// <summary>Stores an Available seat of a class.</summary>
    public async Task PutAvailableSeatAsync(Guid tripId, string seatNo, string seatClass)
    {
        Dictionary<string, AttributeValue> item = Seat(tripId, seatNo, "Available", seatClass);
        await _dynamoDb!.PutItemAsync(new PutItemRequest { TableName = TableName, Item = item });
    }

    /// <summary>Stores a seat Held or Booked by a booking.</summary>
    public async Task PutSeatOfAsync(Guid tripId, string seatNo, string status, Guid bookingId)
    {
        Dictionary<string, AttributeValue> item = Seat(tripId, seatNo, status, "Seater");
        item["bookingId"] = new AttributeValue { S = bookingId.ToString("D") };
        await _dynamoDb!.PutItemAsync(new PutItemRequest { TableName = TableName, Item = item });
    }

    /// <summary>One seat's stored attributes, as plain strings (absent attributes are missing keys).</summary>
    public async Task<Dictionary<string, string>> GetStoredSeatAsync(Guid tripId, string seatNo)
    {
        var key = new Dictionary<string, AttributeValue>
        {
            { "pk", new AttributeValue { S = "TRIP#" + tripId.ToString("D") } },
            { "sk", new AttributeValue { S = "SEAT#" + seatNo } },
        };
        GetItemResponse response = await _dynamoDb!.GetItemAsync(
            new GetItemRequest { TableName = TableName, Key = key, ConsistentRead = true });
        var seat = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, AttributeValue> attribute in response.Item)
        {
            seat[attribute.Key] = attribute.Value.S ?? attribute.Value.N;
        }

        return seat;
    }

    /// <summary>The moves "4C>5C", "4D>5D" as a request body.</summary>
    public static CreateSeatChangeRequest Moves(params string[] moves)
    {
        var changes = new List<RequestedSeatMove?>();
        foreach (string move in moves)
        {
            string[] seats = move.Split('>');
            changes.Add(new RequestedSeatMove(seats[0], seats[1]));
        }

        return new CreateSeatChangeRequest(changes);
    }

    /// <summary>A seat change request with the given internal key (null = header left out) and any body.</summary>
    public static HttpRequestMessage ChangeRequestWith(string tripId, string bookingId, string? internalKey, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/internal/trips/{tripId}/holds/{bookingId}/seatChanges")
        {
            Content = JsonContent.Create(body, options: WireJson.Options),
        };
        if (internalKey != null)
        {
            request.Headers.Add("X-Internal-Key", internalKey);
        }

        return request;
    }

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
    {
        return await _client!.SendAsync(request);
    }

    /// <summary>A seat change as Booking sends it, with the internal key.</summary>
    public async Task<HttpResponseMessage> ChangeAsync(Guid tripId, Guid bookingId, params string[] moves)
    {
        HttpRequestMessage request = ChangeRequestWith(tripId.ToString(), bookingId.ToString(), InternalKey, Moves(moves));
        return await _client!.SendAsync(request);
    }

    public static async Task<CreateSeatChangeResponse> ReadChangeAsync(HttpResponseMessage response)
    {
        CreateSeatChangeResponse? change = await response.Content.ReadFromJsonAsync<CreateSeatChangeResponse>(WireJson.Options);
        return change!;
    }

    public static async Task<CreateSeatChangeProblem> ReadProblemAsync(HttpResponseMessage response)
    {
        string text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);
        JsonElement root = document.RootElement;
        string? code = null;
        if (root.TryGetProperty("code", out JsonElement codeValue))
        {
            code = codeValue.GetString();
        }

        return new CreateSeatChangeProblem
        {
            Status = response.StatusCode,
            Code = code,
            NamedSeats = ReadSeats(root),
            NamedMoves = ReadMoves(root),
            ErrorFields = ReadErrorFields(root),
        };
    }

    private static List<string> ReadSeats(JsonElement root)
    {
        var seats = new List<string>();
        foreach (string member in new[] { "seatNos", "unavailableSeats", "unknownSeats" })
        {
            if (root.TryGetProperty(member, out JsonElement array))
            {
                seats.AddRange(array.EnumerateArray().Select(seat => seat.GetString()!));
            }
        }

        return seats;
    }

    private static List<string> ReadMoves(JsonElement root)
    {
        var moves = new List<string>();
        if (!root.TryGetProperty("changes", out JsonElement changes))
        {
            return moves;
        }

        foreach (JsonElement move in changes.EnumerateArray())
        {
            string from = move.GetProperty("fromSeatNo").GetString()!;
            string to = move.GetProperty("toSeatNo").GetString()!;
            moves.Add(from + ">" + to);
        }

        return moves;
    }

    private static List<string> ReadErrorFields(JsonElement root)
    {
        var fields = new List<string>();
        if (root.TryGetProperty("errors", out JsonElement errors))
        {
            fields.AddRange(errors.EnumerateObject().Select(field => field.Name));
        }

        return fields;
    }

    private static Dictionary<string, AttributeValue> Seat(Guid tripId, string seatNo, string status, string seatClass)
    {
        return new Dictionary<string, AttributeValue>
        {
            { "pk", new AttributeValue { S = "TRIP#" + tripId.ToString("D") } },
            { "sk", new AttributeValue { S = "SEAT#" + seatNo } },
            { "seatNo", new AttributeValue { S = seatNo } },
            { "deck", new AttributeValue { S = "Lower" } },
            { "row", new AttributeValue { N = "1" } },
            { "column", new AttributeValue { N = "1" } },
            { "seatClass", new AttributeValue { S = seatClass } },
            { "fare", new AttributeValue { N = "700" } },
            { "status", new AttributeValue { S = status } },
            { "lastChangeReason", new AttributeValue { S = "Created" } },
            { "lastChangeRequest", new AttributeValue { S = "seed" } },
        };
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
