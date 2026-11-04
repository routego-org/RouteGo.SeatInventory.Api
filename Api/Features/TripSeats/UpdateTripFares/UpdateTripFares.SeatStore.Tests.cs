namespace Api.Features.TripSeats.UpdateTripFares;

/// <summary>A clock that always reads the same instant, so stored change times are exact.</summary>
public sealed class UpdateTripFaresFixedClock : IClock
{
    private readonly DateTimeOffset _utcNow;

    public UpdateTripFaresFixedClock(DateTimeOffset utcNow)
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
public sealed record UpdateTripFaresQueues(string QueueUrl, string DeadLetterQueueUrl);

/// <summary>
/// DynamoDB Local with the seat store table, and LocalStack for the fare-changed queues. The handler and
/// the queue worker are driven directly; trips and seats are seeded and read back raw.
/// </summary>
public sealed class UpdateTripFaresSeatStore : IAsyncLifetime
{
    /// <summary>The instant the worker believes it is.</summary>
    public static readonly DateTimeOffset Now = new DateTimeOffset(2026, 10, 22, 8, 5, 0, TimeSpan.Zero);

    /// <summary>When the usual fare change happened at Fleet.</summary>
    public static readonly DateTimeOffset OccurredAt = new DateTimeOffset(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);

    private const string TableName = "trip-fare-update-tests";

    private readonly DynamoDbContainer _dynamoDbContainer = new DynamoDbBuilder("amazon/dynamodb-local:2.6.1").Build();
    private readonly LocalStackContainer _localStack = new LocalStackBuilder("localstack/localstack:4.4").Build();
    private AmazonDynamoDBClient? _dynamoDb;
    private AmazonSQSClient? _sqs;

    public UpdateTripFaresHandler? Handler { get; private set; }

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

    /// <summary>A FareChanged event as Fleet sends it, that occurred at the given time.</summary>
    public static ReceivedEvent Event(string eventId, DateTimeOffset occurredAt, Dictionary<string, object?> data)
    {
        return new ReceivedEvent
        {
            EventId = eventId,
            EventType = "FareChanged",
            OccurredAt = occurredAt,
            CorrelationId = "fleet-correlation",
            Data = JsonSerializer.SerializeToElement(data),
        };
    }

    /// <summary>FareChanged data setting the fare of the given seats, as Fleet sends it.</summary>
    public static Dictionary<string, object?> FareChanged(Guid tripId, int fare, params string[] seatNos)
    {
        var seats = new List<Dictionary<string, object?>>();
        foreach (string seatNo in seatNos)
        {
            seats.Add(SeatFare(seatNo, fare));
        }

        return new Dictionary<string, object?>
        {
            { "tripId", tripId },
            { "classFares", new List<Dictionary<string, object?>> { new Dictionary<string, object?> { { "class", "Lower berth" }, { "fare", fare } } } },
            { "seats", seats },
        };
    }

    /// <summary>One seat's new fare as FareChanged gives it.</summary>
    public static Dictionary<string, object?> SeatFare(string seatNo, int fare)
    {
        return new Dictionary<string, object?>
        {
            { "seatNo", seatNo },
            { "class", "Lower berth" },
            { "fare", fare },
        };
    }

    /// <summary>Stores a scheduled trip whose seats have been created.</summary>
    public async Task PutScheduledTripAsync(Guid tripId)
    {
        Dictionary<string, AttributeValue> item = TripKey(tripId);
        item["departureAt"] = new AttributeValue { S = "2026-10-24T16:30:00.0000000Z" };
        item["isCancelled"] = new AttributeValue { BOOL = false };
        item["seatsCreatedAt"] = new AttributeValue { S = "2026-10-20T09:00:00.0000000Z" };
        await _dynamoDb!.PutItemAsync(new PutItemRequest { TableName = TableName, Item = item });
    }

    /// <summary>Stores an Available seat at the given fare.</summary>
    public async Task PutAvailableSeatAsync(Guid tripId, string seatNo, int fare)
    {
        await PutSeatAsync(Seat(tripId, seatNo, "Available", fare));
    }

    /// <summary>Stores a seat Held by the booking, at the given fare.</summary>
    public async Task PutHeldSeatAsync(Guid tripId, string seatNo, Guid bookingId, int fare)
    {
        Dictionary<string, AttributeValue> item = Seat(tripId, seatNo, "Held", fare);
        item["bookingId"] = new AttributeValue { S = bookingId.ToString("D") };
        item["holdExpiresAt"] = new AttributeValue { S = "2026-10-22T08:15:00.0000000Z" };
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
    public async Task<UpdateTripFaresQueues> CreateQueuesAsync(string queueName)
    {
        CreateQueueResponse queue = await _sqs!.CreateQueueAsync(queueName);
        CreateQueueResponse deadLetterQueue = await _sqs.CreateQueueAsync(queueName + "-dlq");
        return new UpdateTripFaresQueues(queue.QueueUrl, deadLetterQueue.QueueUrl);
    }

    /// <summary>The queue worker reading the given queues and updating fares in the seat store.</summary>
    public UpdateTripFaresService WorkerOn(UpdateTripFaresQueues queues)
    {
        Microsoft.Extensions.Logging.ILogger<UpdateTripFaresService> logger =
            Microsoft.Extensions.Logging.Abstractions.NullLogger<UpdateTripFaresService>.Instance;
        return new UpdateTripFaresService(HandlerFor(TableName), _sqs!, Configuration(TableName, queues), logger);
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

    private UpdateTripFaresHandler HandlerFor(string tableName)
    {
        Microsoft.Extensions.Logging.ILogger<UpdateTripFaresHandler> logger =
            Microsoft.Extensions.Logging.Abstractions.NullLogger<UpdateTripFaresHandler>.Instance;
        return new UpdateTripFaresHandler(_dynamoDb!, Configuration(tableName, null), new UpdateTripFaresFixedClock(Now), logger);
    }

    private static IConfiguration Configuration(string tableName, UpdateTripFaresQueues? queues)
    {
        var values = new Dictionary<string, string?>
        {
            { "SeatStoreTableName", tableName },
        };
        if (queues != null)
        {
            values["FareChangedQueueUrl"] = queues.QueueUrl;
            values["FareChangedDeadLetterQueueUrl"] = queues.DeadLetterQueueUrl;
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

    private static Dictionary<string, AttributeValue> Seat(Guid tripId, string seatNo, string status, int fare)
    {
        Dictionary<string, AttributeValue> item = SeatKey(tripId, seatNo);
        item["seatNo"] = new AttributeValue { S = seatNo };
        item["status"] = new AttributeValue { S = status };
        item["fare"] = new AttributeValue { N = fare.ToString(System.Globalization.CultureInfo.InvariantCulture) };
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
