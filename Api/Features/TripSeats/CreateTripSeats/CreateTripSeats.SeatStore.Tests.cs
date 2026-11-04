namespace Api.Features.TripSeats.CreateTripSeats;

/// <summary>A clock that always reads the same instant, so stored change times are exact.</summary>
public sealed class CreateTripSeatsFixedClock : IClock
{
    private readonly DateTimeOffset _utcNow;

    public CreateTripSeatsFixedClock(DateTimeOffset utcNow)
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

/// <summary>One test's own queue and its dead-letter queue.</summary>
/// <param name="QueueUrl">Where the test puts its events.</param>
/// <param name="DeadLetterQueueUrl">Where an event that can never be applied is set aside.</param>
public sealed record CreateTripSeatsQueues(string QueueUrl, string DeadLetterQueueUrl);

/// <summary>
/// DynamoDB Local with the seat store table, LocalStack for the trip-scheduled queues, and the API host
/// reading the same table, so a created trip's seat map is read through the real endpoint. The handler
/// and the queue worker are driven directly; trips and seats are read back raw.
/// </summary>
public sealed class CreateTripSeatsSeatStore : IAsyncLifetime
{
    /// <summary>The instant the worker and the API believe it is — before the trips' departure.</summary>
    public static readonly DateTimeOffset Now = new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The departure every announced trip has, as Fleet sends it.</summary>
    public const string DepartureUtc = "2026-10-24T16:30:00Z";

    /// <summary>The operator every announced trip belongs to.</summary>
    public static readonly Guid OperatorId = Guid.Parse("6f1c2a52-8f0e-4c55-9a43-7d2f0d9e5b11");

    // The table name every API host in the test run is configured with; this class's own container has it.
    private const string TableName = "seat-store-tests";

    private readonly DynamoDbContainer _dynamoDbContainer = new DynamoDbBuilder("amazon/dynamodb-local:2.6.1").Build();
    private readonly LocalStackContainer _localStack = new LocalStackBuilder("localstack/localstack:4.4").Build();
    private AmazonDynamoDBClient? _dynamoDb;
    private AmazonSQSClient? _sqs;
    private ApiFactory? _factory;

    public CreateTripSeatsHandler? Handler { get; private set; }

    public HttpClient? Client { get; private set; }

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_dynamoDbContainer.StartAsync(), _localStack.StartAsync());
        // First: DynamoDB Local keeps tables per region, and the clients take theirs from AWS_REGION.
        SetHostEnvironment();
        var credentials = new Amazon.Runtime.BasicAWSCredentials("local", "local");
        _dynamoDb = new AmazonDynamoDBClient(credentials, new AmazonDynamoDBConfig { ServiceURL = _dynamoDbContainer.GetConnectionString() });
        _sqs = new AmazonSQSClient(credentials, new AmazonSQSConfig { ServiceURL = _localStack.GetConnectionString() });
        await CreateTableAsync(_dynamoDb);
        Handler = HandlerFor(TableName);
        StartApi();
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (_factory != null)
        {
            await _factory.DisposeAsync();
        }

        _dynamoDb?.Dispose();
        _sqs?.Dispose();
        await Task.WhenAll(_dynamoDbContainer.DisposeAsync().AsTask(), _localStack.DisposeAsync().AsTask());
    }

    /// <summary>A TripScheduled event as Fleet sends it, with the given seats.</summary>
    public static ReceivedEvent Event(string eventId, Dictionary<string, object?> data)
    {
        return new ReceivedEvent
        {
            EventId = eventId,
            EventType = "TripScheduled",
            OccurredAt = Now,
            CorrelationId = "fleet-correlation",
            Data = JsonSerializer.SerializeToElement(data),
        };
    }

    /// <summary>
    /// TripScheduled data for a trip of the given number of seats: "L1", "L2", … on the Lower deck, one row
    /// of four columns after another, class "Lower berth", all at the given fare. Fleet's fields Seat
    /// Inventory does not read are left out.
    /// </summary>
    public static Dictionary<string, object?> TripScheduled(Guid tripId, int seatCount, int fare)
    {
        var seats = new List<Dictionary<string, object?>>();
        for (int index = 0; index < seatCount; index++)
        {
            seats.Add(SeatData("L" + (index + 1), (index / 4) + 1, (index % 4) + 1, fare));
        }

        return new Dictionary<string, object?>
        {
            { "tripId", tripId },
            { "operatorId", OperatorId.ToString("D") },
            { "departureUtc", DepartureUtc },
            { "seats", seats },
        };
    }

    /// <summary>One seat as TripScheduled gives it.</summary>
    public static Dictionary<string, object?> SeatData(string seatNo, int row, int column, int fare)
    {
        return new Dictionary<string, object?>
        {
            { "seatNo", seatNo },
            { "deck", "Lower" },
            { "row", row },
            { "col", column },
            { "class", "Lower berth" },
            { "fare", fare },
        };
    }

    /// <summary>Remembers a trip known only from a TripCancelled that arrived before its TripScheduled.</summary>
    public async Task PutCancelledTripAsync(Guid tripId)
    {
        Dictionary<string, AttributeValue> item = TripKey(tripId);
        item["isCancelled"] = new AttributeValue { BOOL = true };
        item["cancelledAt"] = new AttributeValue { S = "2026-10-21T08:00:00.0000000Z" };
        await _dynamoDb!.PutItemAsync(new PutItemRequest { TableName = TableName, Item = item });
    }

    /// <summary>Holds a seat for a booking, as CreateHold leaves it.</summary>
    public async Task HoldSeatAsync(Guid tripId, string seatNo, Guid bookingId)
    {
        var request = new UpdateItemRequest
        {
            TableName = TableName,
            Key = SeatKey(tripId, seatNo),
            UpdateExpression = "SET #status = :held, bookingId = :booking, lastChangeReason = :reason",
            ExpressionAttributeNames = new Dictionary<string, string> { { "#status", "status" } },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":held", new AttributeValue { S = "Held" } },
                { ":booking", new AttributeValue { S = bookingId.ToString("D") } },
                { ":reason", new AttributeValue { S = "Held" } },
            },
        };
        await _dynamoDb!.UpdateItemAsync(request);
    }

    /// <summary>The trip's stored attributes, as plain strings; empty when the trip is not known.</summary>
    public async Task<Dictionary<string, string>> GetStoredTripAsync(Guid tripId)
    {
        return await GetStoredItemAsync(TripKey(tripId));
    }

    /// <summary>One seat's stored attributes, as plain strings (absent attributes are missing keys).</summary>
    public async Task<Dictionary<string, string>> GetStoredSeatAsync(Guid tripId, string seatNo)
    {
        return await GetStoredItemAsync(SeatKey(tripId, seatNo));
    }

    /// <summary>Every seat of the trip, as plain strings.</summary>
    public async Task<List<Dictionary<string, string>>> ListStoredSeatsAsync(Guid tripId)
    {
        var query = new QueryRequest
        {
            TableName = TableName,
            KeyConditionExpression = "pk = :pk AND begins_with(sk, :seat)",
            ConsistentRead = true,
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":pk", new AttributeValue { S = "TRIP#" + tripId.ToString("D") } },
                { ":seat", new AttributeValue { S = "SEAT#" } },
            },
        };
        QueryResponse response = await _dynamoDb!.QueryAsync(query);
        var seats = new List<Dictionary<string, string>>();
        foreach (Dictionary<string, AttributeValue> item in response.Items)
        {
            seats.Add(ToText(item));
        }

        return seats;
    }

    /// <summary>A queue and its dead-letter queue of their own for one test, so no test's events reach another.</summary>
    public async Task<CreateTripSeatsQueues> CreateQueuesAsync(string queueName)
    {
        CreateQueueResponse queue = await _sqs!.CreateQueueAsync(queueName);
        CreateQueueResponse deadLetterQueue = await _sqs.CreateQueueAsync(queueName + "-dlq");
        return new CreateTripSeatsQueues(queue.QueueUrl, deadLetterQueue.QueueUrl);
    }

    /// <summary>The queue worker reading the given queues and creating seats in the seat store.</summary>
    public CreateTripSeatsService WorkerOn(CreateTripSeatsQueues queues)
    {
        return WorkerFor(TableName, queues);
    }

    /// <summary>A queue worker whose seat store is unavailable: its table does not exist.</summary>
    public CreateTripSeatsService WorkerWithSeatStoreDownOn(CreateTripSeatsQueues queues)
    {
        return WorkerFor("no-such-table", queues);
    }

    /// <summary>Puts an event on the queue in the RouteGo envelope, as the Fleet subscription delivers it.</summary>
    public async Task SendEventAsync(string queueUrl, ReceivedEvent receivedEvent)
    {
        var envelope = new Dictionary<string, object?>
        {
            { "eventId", receivedEvent.EventId },
            { "eventType", receivedEvent.EventType },
            { "version", 1 },
            { "occurredAt", receivedEvent.OccurredAt },
            { "correlationId", receivedEvent.CorrelationId },
            { "producer", "fleet-service" },
            { "data", receivedEvent.Data },
        };
        string body = JsonSerializer.Serialize(envelope);
        await _sqs!.SendMessageAsync(new SendMessageRequest { QueueUrl = queueUrl, MessageBody = body });
    }

    /// <summary>How many messages are on the queue, waiting or being retried.</summary>
    public async Task<int> CountQueuedMessagesAsync(string queueUrl)
    {
        var request = new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            AttributeNames = new List<string> { "ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible" },
        };
        GetQueueAttributesResponse response = await _sqs!.GetQueueAttributesAsync(request);
        int waiting = int.Parse(response.Attributes["ApproximateNumberOfMessages"], System.Globalization.CultureInfo.InvariantCulture);
        int inFlight = int.Parse(response.Attributes["ApproximateNumberOfMessagesNotVisible"], System.Globalization.CultureInfo.InvariantCulture);
        return waiting + inFlight;
    }

    // The host reads these at startup. They are process-wide and other slices' test classes run alongside
    // this one, so they carry the same values every class sets; the host reaches this class's own
    // container through the replaced DynamoDB client.
    private static void SetHostEnvironment()
    {
        Environment.SetEnvironmentVariable("SeatStoreTableName", TableName);
        Environment.SetEnvironmentVariable("DynamoDbServiceUrl", "http://localhost:1");
        Environment.SetEnvironmentVariable("AWS_REGION", "ap-south-1");
        Environment.SetEnvironmentVariable("IdentityIssuer", "http://localhost:5001");
        Environment.SetEnvironmentVariable("IdentityAudience", "routego");
        Environment.SetEnvironmentVariable("IdentityJwksUrl", "http://localhost:5001/.well-known/jwks.json");
        Environment.SetEnvironmentVariable("HoldExpiryQueueUrl", "http://localhost:1/000000000000/hold-expiry-tests");
        Environment.SetEnvironmentVariable("SqsServiceUrl", "http://localhost:1");
        Environment.SetEnvironmentVariable("routego/internal-key", "seat-inventory-tests-internal-key");
    }

    private void StartApi()
    {
        var fixedClock = new Microsoft.Extensions.DependencyInjection.ServiceDescriptor(typeof(IClock), new CreateTripSeatsFixedClock(Now));
        var seatStore = new Microsoft.Extensions.DependencyInjection.ServiceDescriptor(typeof(IAmazonDynamoDB), _dynamoDb!);
        _factory = new ApiFactory(services => services.Add(fixedClock), services => services.Add(seatStore));
        Client = _factory.CreateClient();
    }

    private CreateTripSeatsHandler HandlerFor(string tableName)
    {
        Microsoft.Extensions.Logging.ILogger<CreateTripSeatsHandler> logger =
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CreateTripSeatsHandler>.Instance;
        return new CreateTripSeatsHandler(_dynamoDb!, Configuration(tableName, null), new CreateTripSeatsFixedClock(Now), logger);
    }

    private CreateTripSeatsService WorkerFor(string tableName, CreateTripSeatsQueues queues)
    {
        Microsoft.Extensions.Logging.ILogger<CreateTripSeatsService> logger =
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CreateTripSeatsService>.Instance;
        return new CreateTripSeatsService(HandlerFor(tableName), _sqs!, Configuration(tableName, queues), logger);
    }

    private static IConfiguration Configuration(string tableName, CreateTripSeatsQueues? queues)
    {
        var values = new Dictionary<string, string?>
        {
            { "SeatStoreTableName", tableName },
        };
        if (queues != null)
        {
            values["TripScheduledQueueUrl"] = queues.QueueUrl;
            values["TripScheduledDeadLetterQueueUrl"] = queues.DeadLetterQueueUrl;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private async Task<Dictionary<string, string>> GetStoredItemAsync(Dictionary<string, AttributeValue> key)
    {
        GetItemResponse response = await _dynamoDb!.GetItemAsync(
            new GetItemRequest { TableName = TableName, Key = key, ConsistentRead = true });
        if (response.Item is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        return ToText(response.Item);
    }

    private static Dictionary<string, string> ToText(Dictionary<string, AttributeValue> item)
    {
        var text = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, AttributeValue> attribute in item)
        {
            text[attribute.Key] = TextOf(attribute.Value);
        }

        return text;
    }

    // Booleans read back as "True" / "False".
    private static string TextOf(AttributeValue value)
    {
        if (value.BOOL != null)
        {
            return value.BOOL.Value.ToString();
        }

        return value.S ?? value.N;
    }

    private static Dictionary<string, AttributeValue> TripKey(Guid tripId)
    {
        return new Dictionary<string, AttributeValue>
        {
            { "pk", new AttributeValue { S = "TRIP#" + tripId.ToString("D") } },
            { "sk", new AttributeValue { S = "TRIP" } },
        };
    }

    private static Dictionary<string, AttributeValue> SeatKey(Guid tripId, string seatNo)
    {
        return new Dictionary<string, AttributeValue>
        {
            { "pk", new AttributeValue { S = "TRIP#" + tripId.ToString("D") } },
            { "sk", new AttributeValue { S = "SEAT#" + seatNo } },
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
