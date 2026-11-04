namespace Api.Features.TripSeats.CreateSeatBlock;

/// <summary>A refused block request, read from its RFC 7807 body.</summary>
public sealed record CreateSeatBlockProblem
{
    public required HttpStatusCode Status { get; init; }

    public required string? Code { get; init; }

    public required IReadOnlyList<string> NamedSeats { get; init; }

    public required IReadOnlyList<string> ErrorFields { get; init; }

    public required string? DepartedAt { get; init; }
}

/// <summary>A clock that always reads the same instant, so departed-or-not never depends on today's date.</summary>
public sealed class CreateSeatBlockFixedClock : IClock
{
    private readonly DateTimeOffset _utcNow;

    public CreateSeatBlockFixedClock(DateTimeOffset utcNow)
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
/// turns into the SeatBlocked event and the audit record.
/// </summary>
public sealed class CreateSeatBlockSeatStore : IAsyncLifetime
{
    /// <summary>The instant the API believes it is — before the default departure in the tests.</summary>
    public static readonly DateTimeOffset Now = new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);

    /// <summary>The departure of every trip unless a test says otherwise.</summary>
    public static readonly DateTimeOffset Departure = new DateTimeOffset(2026, 10, 24, 16, 30, 0, TimeSpan.Zero);

    // Process-wide values every slice's test class sets identically (see GetSeatMap's fixture).
    private const string TableName = "seat-store-tests";
    private const string Issuer = "http://localhost:5001";
    private const string Audience = "routego";

    private static readonly IClock _realClock = new Infrastructure.SystemClock();
    private static readonly RsaSecurityKey _trustedKey = CreateKey("seat-inventory-tests-trusted");

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
        Environment.SetEnvironmentVariable("routego/internal-key", "seat-inventory-tests-internal-key");

        var config = new AmazonDynamoDBConfig { ServiceURL = _container.GetConnectionString() };
        _dynamoDb = new AmazonDynamoDBClient(new Amazon.Runtime.BasicAWSCredentials("local", "local"), config);
        await CreateTableAsync(_dynamoDb);

        var fixedClock = new Microsoft.Extensions.DependencyInjection.ServiceDescriptor(
            typeof(IClock), new CreateSeatBlockFixedClock(Now));
        var seatStore = new Microsoft.Extensions.DependencyInjection.ServiceDescriptor(
            typeof(IAmazonDynamoDB), _dynamoDb);
        _factory = new ApiFactory(
            services => services.Add(fixedClock),
            services => services.Add(seatStore),
            TrustTestSigningKey);
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

    /// <summary>A valid token for an Admin or a Customer.</summary>
    public static string TokenFor(string role)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = Guid.NewGuid().ToString(),
            ["email"] = "person@example.com",
            ["role"] = role,
        };
        return Sign(claims);
    }

    /// <summary>A valid token for a given user of a given operator.</summary>
    public static string OperatorToken(Guid operatorId, Guid userId)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = userId.ToString(),
            ["email"] = "owner@busco.in",
            ["role"] = "Operator",
            ["operatorId"] = operatorId.ToString(),
        };
        return Sign(claims);
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

    /// <summary>Stores a seat as the trip-seat-creation worker and the hold slices leave it.</summary>
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

    public async Task<HttpResponseMessage> BlockAsync(string tripId, string? accessToken, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/trips/{tripId}/seatBlocks")
        {
            Content = JsonContent.Create(body),
        };
        if (accessToken != null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return await _client!.SendAsync(request);
    }

    public async Task<CreateSeatBlockResponse> ReadBlockAsync(HttpResponseMessage response)
    {
        CreateSeatBlockResponse? block = await response.Content.ReadFromJsonAsync<CreateSeatBlockResponse>(WireJson.Options);
        return block!;
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

    public async Task<CreateSeatBlockProblem> BlockRefusedAsync(string tripId, string? accessToken, object body)
    {
        HttpResponseMessage response = await BlockAsync(tripId, accessToken, body);
        string text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);
        JsonElement root = document.RootElement;
        return new CreateSeatBlockProblem
        {
            Status = response.StatusCode,
            Code = ReadString(root, "code"),
            NamedSeats = ReadStrings(root, "unavailableSeats", "unknownSeats"),
            ErrorFields = ReadErrorFields(root),
            DepartedAt = ReadString(root, "departedAt"),
        };
    }

    private static void TrustTestSigningKey(Microsoft.Extensions.DependencyInjection.IServiceCollection services)
    {
        var configuration = new OpenIdConnectConfiguration();
        configuration.SigningKeys.Add(_trustedKey);
        Microsoft.Extensions.DependencyInjection.OptionsServiceCollectionExtensions.PostConfigure<JwtBearerOptions>(
            services,
            JwtBearerDefaults.AuthenticationScheme,
            options => options.ConfigurationManager =
                new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration));
    }

    private static string Sign(Dictionary<string, object> claims)
    {
        // The host checks expiry against real time with no grace, so tokens are minted against it too.
        DateTime now = _realClock.UtcNow.UtcDateTime;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(15),
            Claims = claims,
            SigningCredentials = new SigningCredentials(_trustedKey, SecurityAlgorithms.RsaSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private static RsaSecurityKey CreateKey(string keyId)
    {
        var rsa = RSA.Create(2048);
        return new RsaSecurityKey(rsa) { KeyId = keyId };
    }

    private static string? ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        return value.GetString();
    }

    private static List<string> ReadStrings(JsonElement root, string firstName, string secondName)
    {
        var values = new List<string>();
        if (!root.TryGetProperty(firstName, out JsonElement array) && !root.TryGetProperty(secondName, out array))
        {
            return values;
        }

        foreach (JsonElement value in array.EnumerateArray())
        {
            values.Add(value.GetString()!);
        }

        return values;
    }

    private static List<string> ReadErrorFields(JsonElement root)
    {
        var fields = new List<string>();
        if (!root.TryGetProperty("errors", out JsonElement errors))
        {
            return fields;
        }

        foreach (JsonProperty field in errors.EnumerateObject())
        {
            fields.Add(field.Name);
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
