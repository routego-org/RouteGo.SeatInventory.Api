namespace Api.Features.TripSeats.DeleteSeatBlock;

/// <summary>A refused unblock request, read from its RFC 7807 body.</summary>
public sealed record DeleteSeatBlockProblem
{
    public required HttpStatusCode Status { get; init; }

    public required string? Code { get; init; }

    public required IReadOnlyList<string> NamedSeats { get; init; }

    public required IReadOnlyList<string> ErrorFields { get; init; }

    public required string? DepartedAt { get; init; }
}

/// <summary>A clock that always reads the same instant, so departed-or-not never depends on today's date.</summary>
public sealed class DeleteSeatBlockFixedClock : IClock
{
    private readonly DateTimeOffset _utcNow;

    public DeleteSeatBlockFixedClock(DateTimeOffset utcNow)
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
/// DynamoDB Local in Docker with the seat store table, the API host pointed at it, and access
/// tokens shaped as RouteGo.Identity.Api issues them, signed with a key only this host trusts.
/// Seats are read back raw, because the change record on each seat is what the seat-change stream
/// turns into the SeatUnblocked event and the audit record.
/// </summary>
public sealed class DeleteSeatBlockSeatStore : IAsyncLifetime
{
    /// <summary>The instant the API believes it is — before the default departure in the tests.</summary>
    public static readonly DateTimeOffset Now = new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);

    /// <summary>The departure of every trip unless a test says otherwise.</summary>
    public static readonly DateTimeOffset Departure = new DateTimeOffset(2026, 10, 24, 16, 30, 0, TimeSpan.Zero);

    // Process-wide values every slice's test class sets identically (see GetSeatMap's fixture).
    private const string TableName = "seat-store-tests";

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
        Environment.SetEnvironmentVariable("IdentityIssuer", DeleteSeatBlockTokens.Issuer);
        Environment.SetEnvironmentVariable("IdentityAudience", DeleteSeatBlockTokens.Audience);
        Environment.SetEnvironmentVariable("IdentityJwksUrl", "http://localhost:5001/.well-known/jwks.json");
        Environment.SetEnvironmentVariable("HoldExpiryQueueUrl", "http://localhost:1/000000000000/hold-expiry-tests");
        Environment.SetEnvironmentVariable("SqsServiceUrl", "http://localhost:1");
        Environment.SetEnvironmentVariable("routego/internal-key", "seat-inventory-tests-internal-key");

        var config = new AmazonDynamoDBConfig { ServiceURL = _container.GetConnectionString() };
        _dynamoDb = new AmazonDynamoDBClient(new Amazon.Runtime.BasicAWSCredentials("local", "local"), config);
        await CreateTableAsync(_dynamoDb);

        var fixedClock = new Microsoft.Extensions.DependencyInjection.ServiceDescriptor(typeof(IClock), new DeleteSeatBlockFixedClock(Now));
        var seatStore = new Microsoft.Extensions.DependencyInjection.ServiceDescriptor(typeof(IAmazonDynamoDB), _dynamoDb);
        _factory = new ApiFactory(
            services => services.Add(fixedClock),
            services => services.Add(seatStore),
            DeleteSeatBlockTokens.TrustTestSigningKey);
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

    public async Task PutTripAsync(Guid tripId, Guid operatorId, DateTimeOffset departureAt, bool isCancelled)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            { "pk", new AttributeValue { S = "TRIP#" + tripId.ToString("D") } },
            { "sk", new AttributeValue { S = "TRIP" } },
            { "operatorId", new AttributeValue { S = operatorId.ToString("D") } },
            { "departureAt", new AttributeValue { S = departureAt.UtcDateTime.ToString("O") } },
            { "isCancelled", new AttributeValue { BOOL = isCancelled } },
        };
        await _dynamoDb!.PutItemAsync(new PutItemRequest { TableName = TableName, Item = item });
    }

    /// <summary>Stores a seat as the trip-seat-creation worker, the hold slices and the block slice leave it.</summary>
    public async Task PutSeatAsync(Guid tripId, string seatNo, string status, string? bookingId, string? blockReason)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            { "pk", new AttributeValue { S = "TRIP#" + tripId.ToString("D") } },
            { "sk", new AttributeValue { S = "SEAT#" + seatNo } },
            { "seatNo", new AttributeValue { S = seatNo } },
            { "deck", new AttributeValue { S = "Lower" } },
            { "row", new AttributeValue { N = "1" } },
            { "column", new AttributeValue { N = "1" } },
            { "seatClass", new AttributeValue { S = "Seater" } },
            { "fare", new AttributeValue { N = "700" } },
            { "status", new AttributeValue { S = status } },
            { "lastChangeReason", new AttributeValue { S = "Created" } },
            { "lastChangeRequest", new AttributeValue { S = "seed" } },
        };
        if (bookingId != null)
        {
            item["bookingId"] = new AttributeValue { S = bookingId };
        }

        if (blockReason != null)
        {
            item["blockReason"] = new AttributeValue { S = blockReason };
        }

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

    /// <summary>An unblock with the given token (null = header left out) and raw query value (null = no query).</summary>
    public async Task<HttpResponseMessage> UnblockAsync(string tripId, string? accessToken, string? seatNos)
    {
        string path = $"/trips/{tripId}/seatBlocks";
        if (seatNos != null)
        {
            path += "?seatNos=" + Uri.EscapeDataString(seatNos);
        }

        var request = new HttpRequestMessage(HttpMethod.Delete, path);
        if (accessToken != null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return await _client!.SendAsync(request);
    }

    public async Task<string> GetSeatMapStatusAsync(Guid tripId, string seatNo)
    {
        HttpResponseMessage response = await _client!.GetAsync($"/trips/{tripId}/seats");
        string body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        foreach (JsonElement seat in document.RootElement.GetProperty("seats").EnumerateArray())
        {
            if (seat.GetProperty("seatNo").GetString() == seatNo)
            {
                return seat.GetProperty("status").GetString()!;
            }
        }

        return "missing";
    }

    public async Task<DeleteSeatBlockProblem> UnblockRefusedAsync(string tripId, string? accessToken, string? seatNos)
    {
        HttpResponseMessage response = await UnblockAsync(tripId, accessToken, seatNos);
        string text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);
        JsonElement root = document.RootElement;
        string? code = null;
        if (root.TryGetProperty("code", out JsonElement codeValue))
        {
            code = codeValue.GetString();
        }

        string? departedAt = null;
        if (root.TryGetProperty("departedAt", out JsonElement departedValue))
        {
            departedAt = departedValue.GetString();
        }

        return new DeleteSeatBlockProblem
        {
            Status = response.StatusCode,
            Code = code,
            NamedSeats = ReadStrings(root, "seatNos", "unknownSeats"),
            ErrorFields = ReadErrorFields(root),
            DepartedAt = departedAt,
        };
    }

    private static List<string> ReadStrings(JsonElement root, string firstName, string secondName)
    {
        var values = new List<string>();
        if (!root.TryGetProperty(firstName, out JsonElement array) && !root.TryGetProperty(secondName, out array))
        {
            return values;
        }

        values.AddRange(array.EnumerateArray().Select(value => value.GetString()!));
        return values;
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
