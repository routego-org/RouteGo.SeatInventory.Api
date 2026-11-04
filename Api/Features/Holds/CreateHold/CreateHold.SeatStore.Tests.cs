namespace Api.Features.Holds.CreateHold;

/// <summary>A refused hold request, read from its RFC 7807 body.</summary>
public sealed record CreateHoldProblem
{
    public required HttpStatusCode Status { get; init; }

    public required string? Code { get; init; }

    public required IReadOnlyList<string> NamedSeats { get; init; }

    public required IReadOnlyList<string> ErrorFields { get; init; }

    public required JsonElement? Extension { get; init; }
}

/// <summary>A hold timer as the API handed it to the queue.</summary>
/// <param name="DelaySeconds">How long the queue keeps it back.</param>
/// <param name="Timer">The message body.</param>
public sealed record CreateHoldSentTimer(int DelaySeconds, HoldExpiryTimer Timer);

/// <summary>A clock that always reads the same instant, so hold expiries and departures are exact.</summary>
public sealed class CreateHoldFixedClock : IClock
{
    private readonly DateTimeOffset _utcNow;

    public CreateHoldFixedClock(DateTimeOffset utcNow)
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
/// The hold-expiry queue as the API sees it: every timer is recorded, so a test can say exactly which
/// timers a request started, and then really sent to LocalStack. A booking named as unreachable gets
/// the error a queue outage gives instead.
/// </summary>
public sealed class CreateHoldTimerQueue : AmazonSQSClient
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<SendMessageRequest> _sent = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _unreachableFor = new();

    public CreateHoldTimerQueue(AmazonSQSConfig config)
        : base(new Amazon.Runtime.BasicAWSCredentials("local", "local"), config)
    {
    }

    public void MakeUnreachableFor(Guid bookingId)
    {
        _unreachableFor[bookingId.ToString("D")] = true;
    }

    public List<CreateHoldSentTimer> TimersFor(Guid bookingId)
    {
        var timers = new List<CreateHoldSentTimer>();
        foreach (SendMessageRequest message in _sent)
        {
            HoldExpiryTimer timer = JsonSerializer.Deserialize<HoldExpiryTimer>(message.MessageBody, WireJson.Options)!;
            if (timer.BookingId == bookingId)
            {
                timers.Add(new CreateHoldSentTimer(message.DelaySeconds!.Value, timer));
            }
        }

        return timers;
    }

    public override async Task<SendMessageResponse> SendMessageAsync(
        SendMessageRequest request, CancellationToken cancellationToken = default)
    {
        HoldExpiryTimer timer = JsonSerializer.Deserialize<HoldExpiryTimer>(request.MessageBody, WireJson.Options)!;
        if (_unreachableFor.ContainsKey(timer.BookingId.ToString("D")))
        {
            throw new AmazonSQSException("The hold-expiry queue is unreachable (test).");
        }

        _sent.Enqueue(request);
        return await base.SendMessageAsync(request, cancellationToken);
    }
}

/// <summary>
/// DynamoDB Local and LocalStack in Docker, the seat store table and the hold-expiry queue, and the
/// API host pointed at them. Seats are read back raw, because the change record on each seat is
/// what the seat-change stream turns into the SeatHeld event.
/// </summary>
public sealed class CreateHoldSeatStore : IAsyncLifetime
{
    /// <summary>The instant the API believes it is.</summary>
    public static readonly DateTimeOffset Now = new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);

    /// <summary>The internal key Booking presents — the same value every slice's test class injects.</summary>
    public const string InternalKey = "seat-inventory-tests-internal-key";

    // Process-wide values every slice's test class sets identically (see GetSeatMap's fixture).
    private const string TableName = "seat-store-tests";
    private const string QueueName = "hold-expiry-tests";
    private const string Issuer = "http://localhost:5001";
    private const string Audience = "routego";

    private static readonly IClock _realClock = new Infrastructure.SystemClock();
    private static readonly RsaSecurityKey _trustedKey = CreateKey("seat-inventory-tests-trusted");

    private readonly DynamoDbContainer _dynamoDbContainer = new DynamoDbBuilder("amazon/dynamodb-local:2.6.1").Build();
    private readonly LocalStackContainer _localStack = new LocalStackBuilder("localstack/localstack:4.4").Build();
    private AmazonDynamoDBClient? _dynamoDb;
    private ApiFactory? _factory;
    private HttpClient? _client;
    private string? _queueUrl;

    /// <summary>Every timer the API sent.</summary>
    public CreateHoldTimerQueue? Queue { get; private set; }

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_dynamoDbContainer.StartAsync(), _localStack.StartAsync());

        Environment.SetEnvironmentVariable("SeatStoreTableName", TableName);
        Environment.SetEnvironmentVariable("DynamoDbServiceUrl", "http://localhost:1");
        Environment.SetEnvironmentVariable("AWS_REGION", "ap-south-1");
        Environment.SetEnvironmentVariable("IdentityIssuer", Issuer);
        Environment.SetEnvironmentVariable("IdentityAudience", Audience);
        Environment.SetEnvironmentVariable("IdentityJwksUrl", "http://localhost:5001/.well-known/jwks.json");
        Environment.SetEnvironmentVariable("HoldExpiryQueueUrl", "http://localhost:1/000000000000/hold-expiry-tests");
        Environment.SetEnvironmentVariable("SqsServiceUrl", "http://localhost:1");
        Environment.SetEnvironmentVariable("routego/internal-key", InternalKey);

        var dynamoDbConfig = new AmazonDynamoDBConfig { ServiceURL = _dynamoDbContainer.GetConnectionString() };
        _dynamoDb = new AmazonDynamoDBClient(new Amazon.Runtime.BasicAWSCredentials("local", "local"), dynamoDbConfig);
        await CreateTableAsync(_dynamoDb);
        Queue = new CreateHoldTimerQueue(new AmazonSQSConfig { ServiceURL = _localStack.GetConnectionString() });
        CreateQueueResponse queue = await Queue.CreateQueueAsync(QueueName);
        _queueUrl = queue.QueueUrl;

        var fixedClock = new Microsoft.Extensions.DependencyInjection.ServiceDescriptor(typeof(IClock), new CreateHoldFixedClock(Now));
        var seatStore = new Microsoft.Extensions.DependencyInjection.ServiceDescriptor(typeof(IAmazonDynamoDB), _dynamoDb);
        var timerQueue = new Microsoft.Extensions.DependencyInjection.ServiceDescriptor(typeof(IAmazonSQS), Queue);
        _factory = new ApiFactory(
            services => services.Add(fixedClock),
            services => services.Add(seatStore),
            services => services.Add(timerQueue),
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
        Queue?.Dispose();
        await Task.WhenAll(_dynamoDbContainer.DisposeAsync().AsTask(), _localStack.DisposeAsync().AsTask());
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

    /// <summary>Stores a seat as the trip-seat-creation worker and the hold slices leave it.</summary>
    public async Task PutSeatAsync(Guid tripId, string seatNo, string status, Guid? bookingId)
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
        if (bookingId.HasValue)
        {
            item["bookingId"] = new AttributeValue { S = bookingId.Value.ToString("D") };
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

    /// <summary>A hold request carrying only the credentials given (null = header left out).</summary>
    public static HttpRequestMessage HoldRequestWith(string tripId, object body, string? internalKey, string? accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/internal/trips/{tripId}/holds") { Content = JsonContent.Create(body) };
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

    /// <summary>A hold request as Booking sends it, with the internal key.</summary>
    public async Task<HttpResponseMessage> HoldAsync(string tripId, object body)
    {
        HttpRequestMessage request = HoldRequestWith(tripId, body, InternalKey, null);
        return await _client!.SendAsync(request);
    }

    /// <summary>Every request sent at the same moment.</summary>
    public async Task<HttpResponseMessage[]> HoldAllAtOnceAsync(string tripId, IEnumerable<object> bodies)
    {
        IEnumerable<Task<HttpResponseMessage>> sends = bodies.Select(body => HoldAsync(tripId, body));
        return await Task.WhenAll(sends);
    }

    public static async Task<CreateHoldResponse> ReadHoldAsync(HttpResponseMessage response)
    {
        CreateHoldResponse? hold = await response.Content.ReadFromJsonAsync<CreateHoldResponse>(WireJson.Options);
        return hold!;
    }

    /// <summary>Reads a refusal; <paramref name="extension"/> names the one extra member the test checks.</summary>
    public static async Task<CreateHoldProblem> ReadProblemAsync(HttpResponseMessage response, string extension)
    {
        string text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);
        JsonElement root = document.RootElement.Clone();
        JsonElement? extensionValue = null;
        if (root.TryGetProperty(extension, out JsonElement value))
        {
            extensionValue = value;
        }

        return new CreateHoldProblem
        {
            Status = response.StatusCode,
            Code = ReadCode(root),
            NamedSeats = ReadSeats(root),
            ErrorFields = ReadErrorFields(root),
            Extension = extensionValue,
        };
    }

    /// <summary>Waits for the timer of a booking to come off the real queue, up to the given time.</summary>
    public async Task<HoldExpiryTimer?> ReceiveTimerAsync(Guid bookingId, TimeSpan within)
    {
        DateTime giveUpAt = _realClock.UtcNow.UtcDateTime.Add(within);
        while (_realClock.UtcNow.UtcDateTime < giveUpAt)
        {
            var receive = new ReceiveMessageRequest { QueueUrl = _queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 1 };
            ReceiveMessageResponse received = await Queue!.ReceiveMessageAsync(receive);
            List<Message> messages = received.Messages ?? new List<Message>();
            foreach (Message message in messages)
            {
                HoldExpiryTimer timer = JsonSerializer.Deserialize<HoldExpiryTimer>(message.Body, WireJson.Options)!;
                if (timer.BookingId == bookingId)
                {
                    return timer;
                }
            }
        }

        return null;
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

    private static string? ReadCode(JsonElement root)
    {
        if (!root.TryGetProperty("code", out JsonElement value))
        {
            return null;
        }

        return value.GetString();
    }

    private static List<string> ReadSeats(JsonElement root)
    {
        var seats = new List<string>();
        foreach (string name in new[] { "unavailableSeats", "unknownSeats", "seatNos" })
        {
            if (root.TryGetProperty(name, out JsonElement array))
            {
                seats.AddRange(array.EnumerateArray().Select(seat => seat.GetString()!));
            }
        }

        return seats;
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
