namespace Api.Features.TripSeats.CancelTrip;

/// <summary>A clock that always reads the same instant, so stored change times are exact.</summary>
public sealed class CancelTripFixedClock : IClock
{
    private readonly DateTimeOffset _utcNow;

    public CancelTripFixedClock(DateTimeOffset utcNow)
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
public sealed record CancelTripQueues(string QueueUrl, string DeadLetterQueueUrl);

/// <summary>
/// DynamoDB Local with the seat store table, and LocalStack for the trip-cancelled queues. The handler and
/// the queue worker are driven directly; trips and seats are seeded and read back raw.
/// </summary>
public sealed class CancelTripSeatStore : IAsyncLifetime
{
    /// <summary>The instant the worker believes it is.</summary>
    public static readonly DateTimeOffset Now = new DateTimeOffset(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);

    private const string TableName = "trip-cancellation-tests";

    private readonly DynamoDbContainer _dynamoDbContainer = new DynamoDbBuilder("amazon/dynamodb-local:2.6.1").Build();
    private readonly LocalStackContainer _localStack = new LocalStackBuilder("localstack/localstack:4.4").Build();
    private AmazonDynamoDBClient? _dynamoDb;
    private AmazonSQSClient? _sqs;

    public CancelTripHandler? Handler { get; private set; }

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_dynamoDbContainer.StartAsync(), _localStack.StartAsync());
        // The same region every test class uses: DynamoDB Local keeps tables per region.
        Environment.SetEnvironmentVariable("AWS_REGION", "ap-south-1");
        var credentials = new Amazon.Runtime.BasicAWSCredentials("local", "local");
        _dynamoDb = new AmazonDynamoDBClient(credentials, new AmazonDynamoDBConfig { ServiceURL = _dynamoDbContainer.GetConnectionString() });
        _sqs = new AmazonSQSClient(credentials, new AmazonSQSConfig { ServiceURL = _localStack.GetConnectionString() });
        await CreateTableAsync(_dynamoDb);
        Handler = HandlerFor(TableName);
    }

    public async Task DisposeAsync()
    {
        _dynamoDb?.Dispose();
        _sqs?.Dispose();
        await Task.WhenAll(_dynamoDbContainer.DisposeAsync().AsTask(), _localStack.DisposeAsync().AsTask());
    }

    /// <summary>A TripCancelled event as Fleet sends it.</summary>
    public static ReceivedEvent Event(string eventId, Dictionary<string, object?> data)
    {
        return new ReceivedEvent
        {
            EventId = eventId,
            EventType = "TripCancelled",
            OccurredAt = Now,
            CorrelationId = "fleet-correlation",
            Data = JsonSerializer.SerializeToElement(data),
        };
    }

    /// <summary>TripCancelled data for the trip, with the operator's reason.</summary>
    public static Dictionary<string, object?> TripCancelled(Guid tripId)
    {
        return new Dictionary<string, object?>
        {
            { "tripId", tripId },
            { "reason", "Bus breakdown" },
        };
    }

    /// <summary>Stores a scheduled trip whose seats have been created, still open.</summary>
    public async Task PutScheduledTripAsync(Guid tripId)
    {
        Dictionary<string, AttributeValue> item = TripKey(tripId);
        item["operatorId"] = new AttributeValue { S = Guid.NewGuid().ToString("D") };
        item["departureAt"] = new AttributeValue { S = "2026-10-24T16:30:00.0000000Z" };
        item["isCancelled"] = new AttributeValue { BOOL = false };
        item["seatsCreatedAt"] = new AttributeValue { S = "2026-10-20T09:00:00.0000000Z" };
        await _dynamoDb!.PutItemAsync(new PutItemRequest { TableName = TableName, Item = item });
    }

    /// <summary>Stores an Available seat.</summary>
    public async Task PutAvailableSeatAsync(Guid tripId, string seatNo)
    {
        await PutSeatAsync(Seat(tripId, seatNo, "Available"));
    }

    /// <summary>Stores a seat Blocked for the given reason (Operator or TripCancelled).</summary>
    public async Task PutBlockedSeatAsync(Guid tripId, string seatNo, string blockReason)
    {
        Dictionary<string, AttributeValue> item = Seat(tripId, seatNo, "Blocked");
        item["blockReason"] = new AttributeValue { S = blockReason };
        item["lastChangedBy"] = new AttributeValue { S = Guid.NewGuid().ToString("D") };
        await PutSeatAsync(item);
    }

    /// <summary>Stores a seat Held or Booked by the booking.</summary>
    public async Task PutOwnedSeatAsync(Guid tripId, string seatNo, string status, Guid bookingId)
    {
        Dictionary<string, AttributeValue> item = Seat(tripId, seatNo, status);
        item["bookingId"] = new AttributeValue { S = bookingId.ToString("D") };
        await PutSeatAsync(item);
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

    /// <summary>A queue and its dead-letter queue of their own for one test, so no test's events reach another.</summary>
    public async Task<CancelTripQueues> CreateQueuesAsync(string queueName)
    {
        CreateQueueResponse queue = await _sqs!.CreateQueueAsync(queueName);
        CreateQueueResponse deadLetterQueue = await _sqs.CreateQueueAsync(queueName + "-dlq");
        return new CancelTripQueues(queue.QueueUrl, deadLetterQueue.QueueUrl);
    }

    /// <summary>The queue worker reading the given queues and cancelling trips in the seat store.</summary>
    public CancelTripService WorkerOn(CancelTripQueues queues)
    {
        return WorkerFor(TableName, queues);
    }

    /// <summary>A queue worker whose seat store is unavailable: its table does not exist.</summary>
    public CancelTripService WorkerWithSeatStoreDownOn(CancelTripQueues queues)
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

    private CancelTripHandler HandlerFor(string tableName)
    {
        Microsoft.Extensions.Logging.ILogger<CancelTripHandler> logger =
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CancelTripHandler>.Instance;
        return new CancelTripHandler(_dynamoDb!, Configuration(tableName, null), new CancelTripFixedClock(Now), logger);
    }

    private CancelTripService WorkerFor(string tableName, CancelTripQueues queues)
    {
        Microsoft.Extensions.Logging.ILogger<CancelTripService> logger =
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CancelTripService>.Instance;
        return new CancelTripService(HandlerFor(tableName), _sqs!, Configuration(tableName, queues), logger);
    }

    private static IConfiguration Configuration(string tableName, CancelTripQueues? queues)
    {
        var values = new Dictionary<string, string?>
        {
            { "SeatStoreTableName", tableName },
        };
        if (queues != null)
        {
            values["TripCancelledQueueUrl"] = queues.QueueUrl;
            values["TripCancelledDeadLetterQueueUrl"] = queues.DeadLetterQueueUrl;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private async Task PutSeatAsync(Dictionary<string, AttributeValue> item)
    {
        await _dynamoDb!.PutItemAsync(new PutItemRequest { TableName = TableName, Item = item });
    }

    private async Task<Dictionary<string, string>> GetStoredItemAsync(Dictionary<string, AttributeValue> key)
    {
        GetItemResponse response = await _dynamoDb!.GetItemAsync(
            new GetItemRequest { TableName = TableName, Key = key, ConsistentRead = true });
        var text = new Dictionary<string, string>(StringComparer.Ordinal);
        if (response.Item is null)
        {
            return text;
        }

        foreach (KeyValuePair<string, AttributeValue> attribute in response.Item)
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

    private static Dictionary<string, AttributeValue> Seat(Guid tripId, string seatNo, string status)
    {
        Dictionary<string, AttributeValue> item = SeatKey(tripId, seatNo);
        item["seatNo"] = new AttributeValue { S = seatNo };
        item["status"] = new AttributeValue { S = status };
        item["lastChangeReason"] = new AttributeValue { S = "Created" };
        item["lastChangeRequest"] = new AttributeValue { S = "seed" };
        return item;
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
