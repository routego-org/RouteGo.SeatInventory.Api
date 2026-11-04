namespace Api.Features.Holds.ConfirmHold;

/// <summary>A refused confirm request, read from its RFC 7807 body.</summary>
/// <param name="Status">The response status.</param>
/// <param name="Code">The problem code.</param>
/// <param name="ErrorFields">The fields a validation problem names.</param>
public sealed record ConfirmHoldProblem(HttpStatusCode Status, string? Code, IReadOnlyList<string> ErrorFields);

/// <summary>A clock that always reads the same instant, so expiries and departures are exact.</summary>
public sealed class ConfirmHoldFixedClock : IClock
{
    private readonly DateTimeOffset _utcNow;

    public ConfirmHoldFixedClock(DateTimeOffset utcNow)
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
/// the SeatBooked event.
/// </summary>
public sealed class ConfirmHoldSeatStore : IAsyncLifetime
{
    /// <summary>The instant the API believes it is.</summary>
    public static readonly DateTimeOffset Now = new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);

    /// <summary>The internal key Booking presents — the same value every slice's test class injects.</summary>
    public const string InternalKey = "seat-inventory-tests-internal-key";

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
        Environment.SetEnvironmentVariable("routego/internal-key", InternalKey);

        var config = new AmazonDynamoDBConfig { ServiceURL = _container.GetConnectionString() };
        _dynamoDb = new AmazonDynamoDBClient(new Amazon.Runtime.BasicAWSCredentials("local", "local"), config);
        await CreateTableAsync(_dynamoDb);

        var fixedClock = new Microsoft.Extensions.DependencyInjection.ServiceDescriptor(typeof(IClock), new ConfirmHoldFixedClock(Now));
        var seatStore = new Microsoft.Extensions.DependencyInjection.ServiceDescriptor(typeof(IAmazonDynamoDB), _dynamoDb);
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

    /// <summary>A valid access token, as Identity issues one to an admin.</summary>
    public static string AdminToken()
    {
        DateTime now = _realClock.UtcNow.UtcDateTime;
        var claims = new Dictionary<string, object> { ["sub"] = Guid.NewGuid().ToString(), ["role"] = "Admin" };
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

    /// <summary>Stores a seat Held by a booking, as a hold leaves it.</summary>
    public async Task PutHeldSeatAsync(Guid tripId, string seatNo, Guid bookingId, DateTimeOffset holdExpiresAt)
    {
        Dictionary<string, AttributeValue> item = Seat(tripId, seatNo, "Held");
        item["bookingId"] = new AttributeValue { S = bookingId.ToString("D") };
        item["holdExpiresAt"] = new AttributeValue { S = holdExpiresAt.UtcDateTime.ToString("O") };
        await _dynamoDb!.PutItemAsync(new PutItemRequest { TableName = TableName, Item = item });
    }

    /// <summary>Stores a seat no booking holds.</summary>
    public async Task PutAvailableSeatAsync(Guid tripId, string seatNo)
    {
        Dictionary<string, AttributeValue> item = Seat(tripId, seatNo, "Available");
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

    /// <summary>A confirm request carrying only the credentials given (null = header left out).</summary>
    public static HttpRequestMessage ConfirmRequestWith(string tripId, string bookingId, string? internalKey, string? accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/internal/trips/{tripId}/holds/{bookingId}/confirm");
        if (internalKey != null)
        {
            request.Headers.Add("X-Internal-Key", internalKey);
        }

        if (accessToken != null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return request;
    }

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
    {
        return await _client!.SendAsync(request);
    }

    /// <summary>A confirm as Booking sends it, with the internal key.</summary>
    public async Task<HttpResponseMessage> ConfirmAsync(Guid tripId, Guid bookingId)
    {
        HttpRequestMessage request = ConfirmRequestWith(tripId.ToString(), bookingId.ToString(), InternalKey, null);
        return await _client!.SendAsync(request);
    }

    public static async Task<ConfirmHoldResponse> ReadHoldAsync(HttpResponseMessage response)
    {
        ConfirmHoldResponse? hold = await response.Content.ReadFromJsonAsync<ConfirmHoldResponse>(WireJson.Options);
        return hold!;
    }

    public static async Task<ConfirmHoldProblem> ReadProblemAsync(HttpResponseMessage response)
    {
        string text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);
        JsonElement root = document.RootElement;
        string? code = null;
        if (root.TryGetProperty("code", out JsonElement codeValue))
        {
            code = codeValue.GetString();
        }

        var fields = new List<string>();
        if (root.TryGetProperty("errors", out JsonElement errors))
        {
            fields.AddRange(errors.EnumerateObject().Select(field => field.Name));
        }

        return new ConfirmHoldProblem(response.StatusCode, code, fields);
    }

    private static Dictionary<string, AttributeValue> Seat(Guid tripId, string seatNo, string status)
    {
        return new Dictionary<string, AttributeValue>
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
            { "lastChangeReason", new AttributeValue { S = "Held" } },
            { "lastChangeRequest", new AttributeValue { S = "seed" } },
        };
    }

    private static void TrustTestSigningKey(Microsoft.Extensions.DependencyInjection.IServiceCollection services)
    {
        var configuration = new OpenIdConnectConfiguration();
        configuration.SigningKeys.Add(_trustedKey);
        Microsoft.Extensions.DependencyInjection.OptionsServiceCollectionExtensions.PostConfigure<JwtBearerOptions>(
            services,
            JwtBearerDefaults.AuthenticationScheme,
            options => options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration));
    }

    private static RsaSecurityKey CreateKey(string keyId)
    {
        var rsa = RSA.Create(2048);
        return new RsaSecurityKey(rsa) { KeyId = keyId };
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
