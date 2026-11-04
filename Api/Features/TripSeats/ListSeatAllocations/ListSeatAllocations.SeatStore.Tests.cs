namespace Api.Features.TripSeats.ListSeatAllocations;

/// <summary>A refused seat allocations request, read from its RFC 7807 body.</summary>
public sealed record ListSeatAllocationsProblem(HttpStatusCode Status, string? Code, string? Type);

/// <summary>A seat as the seat store holds it, including who holds or owns it.</summary>
public sealed record ListSeatAllocationsStoredSeat
{
    public required string SeatNo { get; init; }

    public required string Deck { get; init; }

    public required int Row { get; init; }

    public required int Column { get; init; }

    public required string Status { get; init; }

    public string? BookingId { get; init; }

    public DateTimeOffset? HoldExpiresAt { get; init; }

    public string? BlockReason { get; init; }
}

/// <summary>A clock that always reads the same instant, so departed-or-not never depends on today's date.</summary>
public sealed class ListSeatAllocationsFixedClock : IClock
{
    private readonly DateTimeOffset _utcNow;

    public ListSeatAllocationsFixedClock(DateTimeOffset utcNow)
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
/// tokens shaped exactly as RouteGo.Identity.Api issues them (RS256; sub, email, role and, for
/// operators, operatorId), signed with a key only this host trusts — so the tests exercise the real
/// bearer validation without an Identity instance to download keys from.
/// </summary>
public sealed class ListSeatAllocationsSeatStore : IAsyncLifetime
{
    /// <summary>The instant the API believes it is — before the trips' departure in the tests.</summary>
    public static readonly DateTimeOffset Now = new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);

    // Process-wide values every slice's test class sets identically (see GetSeatMap's fixture).
    private const string TableName = "seat-store-tests";
    private const string Issuer = "http://localhost:5001";
    private const string Audience = "routego";

    private static readonly IClock _realClock = new Infrastructure.SystemClock();
    private static readonly RsaSecurityKey _trustedKey = CreateKey("seat-inventory-tests-trusted");
    private static readonly RsaSecurityKey _untrustedKey = CreateKey("seat-inventory-tests-untrusted");

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
            typeof(IClock), new ListSeatAllocationsFixedClock(Now));
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
        return Sign(claims, _trustedKey);
    }

    /// <summary>A valid token for a user of the given operator.</summary>
    public static string OperatorToken(Guid operatorId)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = Guid.NewGuid().ToString(),
            ["email"] = "owner@busco.in",
            ["role"] = "Operator",
            ["operatorId"] = operatorId.ToString(),
        };
        return Sign(claims, _trustedKey);
    }

    /// <summary>A well-formed Admin token signed by a key Seat Inventory does not trust.</summary>
    public static string UntrustedAdminToken()
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = Guid.NewGuid().ToString(),
            ["email"] = "person@example.com",
            ["role"] = "Admin",
        };
        return Sign(claims, _untrustedKey);
    }

    public static ListSeatAllocationsStoredSeat Seat(string seatNo, int row, int column, string status)
    {
        return new ListSeatAllocationsStoredSeat
        {
            SeatNo = seatNo,
            Deck = "Lower",
            Row = row,
            Column = column,
            Status = status,
        };
    }

    public async Task PutTripAsync(Guid tripId, Guid operatorId, bool isCancelled)
    {
        var departureAt = new DateTimeOffset(2026, 10, 24, 16, 30, 0, TimeSpan.Zero);
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

    public async Task PutSeatAsync(Guid tripId, ListSeatAllocationsStoredSeat seat)
    {
        Dictionary<string, AttributeValue> item = ToItem(tripId, seat);
        await _dynamoDb!.PutItemAsync(new PutItemRequest { TableName = TableName, Item = item });
    }

    public async Task<HttpResponseMessage> SendAsync(string tripId, string? accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/trips/{tripId}/seatAllocations");
        if (accessToken != null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return await _client!.SendAsync(request);
    }

    public async Task<ListSeatAllocationsResponse> GetAllocationsAsync(Guid tripId, string accessToken)
    {
        HttpResponseMessage response = await SendAsync(tripId.ToString(), accessToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ListSeatAllocationsResponse? allocations =
            await response.Content.ReadFromJsonAsync<ListSeatAllocationsResponse>(WireJson.Options);
        return allocations!;
    }

    public async Task<string> GetRawAllocationsAsync(Guid tripId, string accessToken)
    {
        HttpResponseMessage response = await SendAsync(tripId.ToString(), accessToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    public async Task<ListSeatAllocationsProblem> GetProblemAsync(string tripId, string? accessToken)
    {
        HttpResponseMessage response = await SendAsync(tripId, accessToken);
        string body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        return new ListSeatAllocationsProblem(response.StatusCode, ReadString(root, "code"), ReadString(root, "type"));
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

    private static string Sign(Dictionary<string, object> claims, RsaSecurityKey key)
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
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
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

    private static Dictionary<string, AttributeValue> ToItem(Guid tripId, ListSeatAllocationsStoredSeat seat)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            { "pk", new AttributeValue { S = "TRIP#" + tripId.ToString("D") } },
            { "sk", new AttributeValue { S = "SEAT#" + seat.SeatNo } },
            { "seatNo", new AttributeValue { S = seat.SeatNo } },
            { "deck", new AttributeValue { S = seat.Deck } },
            { "row", new AttributeValue { N = seat.Row.ToString(System.Globalization.CultureInfo.InvariantCulture) } },
            { "column", new AttributeValue { N = seat.Column.ToString(System.Globalization.CultureInfo.InvariantCulture) } },
            { "seatClass", new AttributeValue { S = "Seater" } },
            { "fare", new AttributeValue { N = "700" } },
            { "status", new AttributeValue { S = seat.Status } },
        };
        if (seat.BookingId != null)
        {
            item["bookingId"] = new AttributeValue { S = seat.BookingId };
        }

        if (seat.HoldExpiresAt != null)
        {
            item["holdExpiresAt"] = new AttributeValue { S = seat.HoldExpiresAt.Value.UtcDateTime.ToString("O") };
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
