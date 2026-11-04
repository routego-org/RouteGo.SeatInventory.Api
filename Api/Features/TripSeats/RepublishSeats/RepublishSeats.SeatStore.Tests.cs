namespace Api.Features.TripSeats.RepublishSeats;

/// <summary>A clock that always reads the same instant, so a trip is upcoming or departed exactly as seeded.</summary>
public sealed class RepublishSeatsFixedClock : IClock
{
    private readonly DateTimeOffset _utcNow;

    public RepublishSeatsFixedClock(DateTimeOffset utcNow)
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

/// <summary>One seat to put in the seat store, with only the attributes its status has.</summary>
public sealed record RepublishSeatsSeed
{
    public required string SeatNo { get; init; }

    public required string Status { get; init; }

    public required string ChangedAt { get; init; }

    public Guid? BookingId { get; init; }

    public string? HoldExpiresAt { get; init; }

    public string? BlockReason { get; init; }
}

/// <summary>One event as a subscriber receives it: the eventType message attribute and the envelope.</summary>
/// <param name="EventTypeAttribute">The eventType message attribute.</param>
/// <param name="Envelope">The RouteGo envelope.</param>
public sealed record RepublishedSeatEvent(string? EventTypeAttribute, JsonElement Envelope);

/// <summary>
/// DynamoDB Local with the seat store table and LocalStack with the inventory events topic, delivering
/// to a queue the tests read as a subscriber would. Trips and seats are seeded and read back raw.
/// </summary>
public sealed class RepublishSeatsSeatStore : IAsyncLifetime
{
    /// <summary>The instant the job believes it is.</summary>
    public static readonly DateTimeOffset Now = new DateTimeOffset(2026, 10, 24, 11, 0, 0, TimeSpan.Zero);

    /// <summary>A departure still to come at <see cref="Now"/>.</summary>
    public const string UpcomingDeparture = "2026-10-25T10:00:00.0000000Z";

    /// <summary>A departure already past at <see cref="Now"/>.</summary>
    public const string PastDeparture = "2026-10-23T10:00:00.0000000Z";

    private const string TableName = "seat-republish-tests";

    private readonly DynamoDbContainer _dynamoDbContainer = new DynamoDbBuilder("amazon/dynamodb-local:2.6.1").Build();
    private readonly LocalStackContainer _localStack = new LocalStackBuilder("localstack/localstack:4.4").Build();
    private AmazonDynamoDBClient? _dynamoDb;
    private Amazon.SimpleNotificationService.AmazonSimpleNotificationServiceClient? _sns;
    private AmazonSQSClient? _sqs;
    private string? _queueUrl;
    private string? _topicArn;

    public RepublishSeatsHandler? Handler { get; private set; }

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_dynamoDbContainer.StartAsync(), _localStack.StartAsync());
        var credentials = new Amazon.Runtime.BasicAWSCredentials("local", "local");
        _dynamoDb = new AmazonDynamoDBClient(credentials, new AmazonDynamoDBConfig { ServiceURL = _dynamoDbContainer.GetConnectionString() });
        var snsConfig = new Amazon.SimpleNotificationService.AmazonSimpleNotificationServiceConfig { ServiceURL = _localStack.GetConnectionString() };
        _sns = new Amazon.SimpleNotificationService.AmazonSimpleNotificationServiceClient(credentials, snsConfig);
        _sqs = new AmazonSQSClient(credentials, new AmazonSQSConfig { ServiceURL = _localStack.GetConnectionString() });

        await CreateTableAsync(_dynamoDb);
        _topicArn = await CreateTopicAsync("republish-inventory-events-tests");
        _queueUrl = await SubscribeQueueAsync(_topicArn, "republish-subscriber-tests");
        Handler = HandlerFor(_topicArn);
    }

    public async Task DisposeAsync()
    {
        _dynamoDb?.Dispose();
        _sns?.Dispose();
        _sqs?.Dispose();
        await Task.WhenAll(_dynamoDbContainer.DisposeAsync().AsTask(), _localStack.DisposeAsync().AsTask());
    }

    /// <summary>The same job, sending to a topic that does not exist — the messaging service "down".</summary>
    public RepublishSeatsHandler HandlerDuringOutage()
    {
        return HandlerFor("arn:aws:sns:ap-south-1:000000000000:no-such-topic");
    }

    public async Task PutTripAsync(Guid tripId, string departureAt, bool isCancelled = false)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            { "pk", new AttributeValue { S = "TRIP#" + tripId.ToString("D") } },
            { "sk", new AttributeValue { S = "TRIP" } },
            { "operatorId", new AttributeValue { S = OperatorId(tripId) } },
            { "departureAt", new AttributeValue { S = departureAt } },
        };
        if (isCancelled)
        {
            item["isCancelled"] = new AttributeValue { BOOL = true };
        }

        await _dynamoDb!.PutItemAsync(new PutItemRequest { TableName = TableName, Item = item });
    }

    /// <summary>The operator a seeded trip belongs to, derived from the trip so each trip has its own.</summary>
    public static string OperatorId(Guid tripId)
    {
        return "operator-of-" + tripId.ToString("N").Substring(0, 8);
    }

    public async Task PutSeatAsync(Guid tripId, RepublishSeatsSeed seat)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            { "pk", new AttributeValue { S = "TRIP#" + tripId.ToString("D") } },
            { "sk", new AttributeValue { S = "SEAT#" + seat.SeatNo } },
            { "seatNo", new AttributeValue { S = seat.SeatNo } },
            { "status", new AttributeValue { S = seat.Status } },
            { "lastChangedAt", new AttributeValue { S = seat.ChangedAt } },
        };
        if (seat.BookingId.HasValue)
        {
            item["bookingId"] = new AttributeValue { S = seat.BookingId.Value.ToString("D") };
        }

        if (seat.HoldExpiresAt != null)
        {
            item["holdExpiresAt"] = new AttributeValue { S = seat.HoldExpiresAt };
        }

        if (seat.BlockReason != null)
        {
            item["blockReason"] = new AttributeValue { S = seat.BlockReason };
        }

        await _dynamoDb!.PutItemAsync(new PutItemRequest { TableName = TableName, Item = item });
    }

    /// <summary>Every record of the trip as stored, ordered by sort key.</summary>
    public async Task<List<Dictionary<string, AttributeValue>>> ReadTripAsync(Guid tripId)
    {
        var query = new QueryRequest
        {
            TableName = TableName,
            KeyConditionExpression = "pk = :pk",
            ConsistentRead = true,
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":pk", new AttributeValue { S = "TRIP#" + tripId.ToString("D") } },
            },
        };
        QueryResponse response = await _dynamoDb!.QueryAsync(query);
        return response.Items;
    }

    /// <summary>The seat events a subscriber received for one trip since the last call.</summary>
    public async Task<List<RepublishedSeatEvent>> ReceiveSeatEventsAsync(Guid tripId)
    {
        List<RepublishedSeatEvent> received = await ReceiveAllAsync();
        string trip = tripId.ToString("D");
        return received.FindAll(seatEvent => seatEvent.Envelope.GetProperty("data").GetProperty("tripId").GetString() == trip);
    }

    private RepublishSeatsHandler HandlerFor(string topicArn)
    {
        var values = new Dictionary<string, string?>
        {
            { "SeatStoreTableName", TableName },
            { "InventoryEventsTopicArn", topicArn },
            { "AuditEventsTopicArn", topicArn },
        };
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var sender = new SeatEventSender(_sns!, configuration);
        return new RepublishSeatsHandler(_dynamoDb!, configuration, sender, new RepublishSeatsFixedClock(Now));
    }

    private async Task<List<RepublishedSeatEvent>> ReceiveAllAsync()
    {
        var received = new List<RepublishedSeatEvent>();
        var request = new ReceiveMessageRequest
        {
            QueueUrl = _queueUrl,
            MaxNumberOfMessages = 10,
            WaitTimeSeconds = 2,
            MessageAttributeNames = new List<string> { "All" },
        };
        ReceiveMessageResponse response = await _sqs!.ReceiveMessageAsync(request);
        while (response.Messages != null && response.Messages.Count > 0)
        {
            foreach (Message message in response.Messages)
            {
                received.Add(ToPublishedEvent(message));
                await _sqs.DeleteMessageAsync(_queueUrl, message.ReceiptHandle);
            }

            response = await _sqs.ReceiveMessageAsync(request);
        }

        return received;
    }

    private static RepublishedSeatEvent ToPublishedEvent(Message message)
    {
        string? eventType = null;
        if (message.MessageAttributes != null && message.MessageAttributes.ContainsKey("eventType"))
        {
            eventType = message.MessageAttributes["eventType"].StringValue;
        }

        using var document = JsonDocument.Parse(message.Body);
        return new RepublishedSeatEvent(eventType, document.RootElement.Clone());
    }

    private async Task<string> CreateTopicAsync(string name)
    {
        var request = new Amazon.SimpleNotificationService.Model.CreateTopicRequest { Name = name };
        Amazon.SimpleNotificationService.Model.CreateTopicResponse response = await _sns!.CreateTopicAsync(request);
        return response.TopicArn;
    }

    // Raw delivery: the queue receives the envelope itself, as Search's subscription does.
    private async Task<string> SubscribeQueueAsync(string topicArn, string queueName)
    {
        CreateQueueResponse queue = await _sqs!.CreateQueueAsync(queueName);
        var attributes = new GetQueueAttributesRequest { QueueUrl = queue.QueueUrl, AttributeNames = new List<string> { "QueueArn" } };
        GetQueueAttributesResponse queueAttributes = await _sqs.GetQueueAttributesAsync(attributes);
        var subscribe = new Amazon.SimpleNotificationService.Model.SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = queueAttributes.Attributes["QueueArn"],
            Attributes = new Dictionary<string, string> { { "RawMessageDelivery", "true" } },
        };
        await _sns!.SubscribeAsync(subscribe);
        return queue.QueueUrl;
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
