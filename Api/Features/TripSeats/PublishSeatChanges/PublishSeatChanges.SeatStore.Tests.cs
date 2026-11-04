namespace Api.Features.TripSeats.PublishSeatChanges;

/// <summary>A clock a test can move, so a change can be made to look minutes old.</summary>
public sealed class PublishSeatChangesTestClock : IClock
{
    private static readonly IClock _realClock = new Infrastructure.SystemClock();

    public DateTimeOffset UtcNow { get; set; } = RealNow();

    /// <summary>The real time now, which the change stream stamps its records with.</summary>
    public static DateTimeOffset RealNow()
    {
        return _realClock.UtcNow;
    }
}

/// <summary>One event as a subscriber receives it: the eventType message attribute and the envelope.</summary>
/// <param name="EventTypeAttribute">The eventType message attribute.</param>
/// <param name="Envelope">The RouteGo envelope.</param>
public sealed record PublishedSeatEvent(string? EventTypeAttribute, JsonElement Envelope);

/// <summary>
/// DynamoDB Local with the seat store table and its change stream turned on, and LocalStack with the
/// two topics, each delivering to a queue the tests read as a subscriber would. Seats are changed by
/// writing the change record straight onto them, exactly as the write slices' SPEC.md describes it.
/// One publisher handler is shared by the whole class, because it holds the lease.
/// </summary>
public sealed class PublishSeatChangesSeatStore : IAsyncLifetime
{
    /// <summary>When every recorded change in these tests says it was made.</summary>
    public const string ChangedAt = "2026-10-24T10:00:00.0000000Z";

    private const string TableName = "seat-publisher-tests";

    private readonly DynamoDbContainer _dynamoDbContainer = new DynamoDbBuilder("amazon/dynamodb-local:2.6.1").Build();
    private readonly LocalStackContainer _localStack = new LocalStackBuilder("localstack/localstack:4.4").Build();
    private AmazonDynamoDBClient? _dynamoDb;
    private Amazon.DynamoDBStreams.AmazonDynamoDBStreamsClient? _streams;
    private Amazon.SimpleNotificationService.AmazonSimpleNotificationServiceClient? _sns;
    private AmazonSQSClient? _sqs;
    private string? _inventoryQueueUrl;
    private string? _auditQueueUrl;
    private string? _inventoryTopicArn;
    private string? _auditTopicArn;
    private SeatPublisherProgress? _progress;
    private SeatChangeStream? _stream;

    public PublishSeatChangesTestClock Clock { get; } = new PublishSeatChangesTestClock();

    /// <summary>The publisher, holding the lease for the whole class.</summary>
    public PublishSeatChangesHandler? Handler { get; private set; }

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_dynamoDbContainer.StartAsync(), _localStack.StartAsync());
        var credentials = new Amazon.Runtime.BasicAWSCredentials("local", "local");
        _dynamoDb = new AmazonDynamoDBClient(credentials, new AmazonDynamoDBConfig { ServiceURL = _dynamoDbContainer.GetConnectionString() });
        var streamsConfig = new Amazon.DynamoDBStreams.AmazonDynamoDBStreamsConfig { ServiceURL = _dynamoDbContainer.GetConnectionString() };
        _streams = new Amazon.DynamoDBStreams.AmazonDynamoDBStreamsClient(credentials, streamsConfig);
        var snsConfig = new Amazon.SimpleNotificationService.AmazonSimpleNotificationServiceConfig { ServiceURL = _localStack.GetConnectionString() };
        _sns = new Amazon.SimpleNotificationService.AmazonSimpleNotificationServiceClient(credentials, snsConfig);
        _sqs = new AmazonSQSClient(credentials, new AmazonSQSConfig { ServiceURL = _localStack.GetConnectionString() });

        await CreateTableAsync(_dynamoDb);
        _inventoryTopicArn = await CreateTopicAsync("inventory-events-tests");
        _auditTopicArn = await CreateTopicAsync("audit-events-tests");
        _inventoryQueueUrl = await SubscribeQueueAsync(_inventoryTopicArn, "inventory-subscriber-tests");
        _auditQueueUrl = await SubscribeQueueAsync(_auditTopicArn, "audit-subscriber-tests");

        IConfiguration configuration = Configuration(_inventoryTopicArn);
        _progress = new SeatPublisherProgress(_dynamoDb, configuration, Clock);
        _stream = new SeatChangeStream(_dynamoDb, _streams, configuration);
        Handler = new PublishSeatChangesHandler(_progress, _stream, new SeatEventSender(_sns, configuration), Clock);
    }

    public async Task DisposeAsync()
    {
        _dynamoDb?.Dispose();
        _streams?.Dispose();
        _sns?.Dispose();
        _sqs?.Dispose();
        await Task.WhenAll(_dynamoDbContainer.DisposeAsync().AsTask(), _localStack.DisposeAsync().AsTask());
    }

    /// <summary>The same publisher, but sending seat events to a topic that does not exist — the messaging service "down".</summary>
    public PublishSeatChangesHandler HandlerDuringOutage()
    {
        IConfiguration configuration = Configuration("arn:aws:sns:us-east-1:000000000000:no-such-topic");
        return new PublishSeatChangesHandler(_progress!, _stream!, new SeatEventSender(_sns!, configuration), Clock);
    }

    /// <summary>A publisher on another running copy of the API, with its own lease owner.</summary>
    public PublishSeatChangesHandler HandlerOfAnotherCopy()
    {
        IConfiguration configuration = Configuration(_inventoryTopicArn!);
        var progress = new SeatPublisherProgress(_dynamoDb!, configuration, Clock);
        return new PublishSeatChangesHandler(progress, _stream!, new SeatEventSender(_sns!, configuration), Clock);
    }

    /// <summary>Forgets how far every shard was published, as if no delivery had ever been confirmed.</summary>
    public async Task ForgetProgressAsync()
    {
        Dictionary<string, ShardProgress> shards = await _progress!.ListShardsAsync(CancellationToken.None);
        foreach (string shardId in shards.Keys)
        {
            var key = new Dictionary<string, AttributeValue>
            {
                { "pk", new AttributeValue { S = SeatPublisherProgress.PartitionKey } },
                { "sk", new AttributeValue { S = "SHARD#" + shardId } },
            };
            await _dynamoDb!.DeleteItemAsync(new DeleteItemRequest { TableName = TableName, Key = key });
        }
    }

    public async Task PutTripAsync(Guid tripId, Guid operatorId)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            { "pk", new AttributeValue { S = "TRIP#" + tripId.ToString("D") } },
            { "sk", new AttributeValue { S = "TRIP" } },
            { "operatorId", new AttributeValue { S = operatorId.ToString("D") } },
            { "departureAt", new AttributeValue { S = "2026-10-30T10:00:00.0000000Z" } },
        };
        await _dynamoDb!.PutItemAsync(new PutItemRequest { TableName = TableName, Item = item });
    }

    /// <summary>Creates a seat — something no subscriber is told about.</summary>
    public async Task PutSeatAsync(Guid tripId, string seatNo, string status, Guid? bookingId)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            { "pk", new AttributeValue { S = "TRIP#" + tripId.ToString("D") } },
            { "sk", new AttributeValue { S = "SEAT#" + seatNo } },
            { "seatNo", new AttributeValue { S = seatNo } },
            { "status", new AttributeValue { S = status } },
        };
        if (bookingId.HasValue)
        {
            item["bookingId"] = new AttributeValue { S = bookingId.Value.ToString("D") };
        }

        await _dynamoDb!.PutItemAsync(new PutItemRequest { TableName = TableName, Item = item });
    }

    /// <summary>
    /// Changes seats in one transaction, as one request does: every attribute given is set, and every
    /// attribute given as null is removed.
    /// </summary>
    public async Task ChangeSeatsAsync(Guid tripId, IReadOnlyList<string> seatNos, Dictionary<string, string?> record)
    {
        var writes = new List<TransactWriteItem>();
        foreach (string seatNo in seatNos)
        {
            writes.Add(ChangeSeat(tripId, seatNo, record));
        }

        await _dynamoDb!.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = writes });
    }

    /// <summary>The seat events a subscriber received for one trip since the last call.</summary>
    public async Task<List<PublishedSeatEvent>> ReceiveSeatEventsAsync(Guid tripId)
    {
        List<PublishedSeatEvent> received = await ReceiveAllAsync(_inventoryQueueUrl!);
        string trip = tripId.ToString("D");
        return received.FindAll(seatEvent => seatEvent.Envelope.GetProperty("data").GetProperty("tripId").GetString() == trip);
    }

    /// <summary>The audit records the audit log received for one trip since the last call.</summary>
    public async Task<List<PublishedSeatEvent>> ReceiveAuditRecordsAsync(Guid tripId)
    {
        List<PublishedSeatEvent> received = await ReceiveAllAsync(_auditQueueUrl!);
        string trip = tripId.ToString("D");
        return received.FindAll(audit => audit.Envelope.GetProperty("data").GetProperty("entityId").GetString() == trip);
    }

    private static TransactWriteItem ChangeSeat(Guid tripId, string seatNo, Dictionary<string, string?> record)
    {
        var names = new Dictionary<string, string>();
        var values = new Dictionary<string, AttributeValue>();
        string expression = ToUpdateExpression(record, names, values);
        var key = new Dictionary<string, AttributeValue>
        {
            { "pk", new AttributeValue { S = "TRIP#" + tripId.ToString("D") } },
            { "sk", new AttributeValue { S = "SEAT#" + seatNo } },
        };
        var update = new Update
        {
            TableName = TableName,
            Key = key,
            UpdateExpression = expression,
            ExpressionAttributeNames = names,
            ExpressionAttributeValues = values,
        };
        return new TransactWriteItem { Update = update };
    }

    // Placeholders for every name, since STATUS is a DynamoDB reserved word.
    private static string ToUpdateExpression(
        Dictionary<string, string?> record, Dictionary<string, string> names, Dictionary<string, AttributeValue> values)
    {
        var sets = new List<string>();
        var removes = new List<string>();
        int index = 0;
        foreach (KeyValuePair<string, string?> attribute in record)
        {
            string name = "#a" + index;
            names[name] = attribute.Key;
            if (attribute.Value is null)
            {
                removes.Add(name);
            }
            else
            {
                values[":v" + index] = new AttributeValue { S = attribute.Value };
                sets.Add(name + " = :v" + index);
            }

            index++;
        }

        string expression = "SET " + string.Join(", ", sets);
        if (removes.Count > 0)
        {
            expression += " REMOVE " + string.Join(", ", removes);
        }

        return expression;
    }

    private async Task<List<PublishedSeatEvent>> ReceiveAllAsync(string queueUrl)
    {
        var received = new List<PublishedSeatEvent>();
        var request = new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
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
                await _sqs.DeleteMessageAsync(queueUrl, message.ReceiptHandle);
            }

            response = await _sqs.ReceiveMessageAsync(request);
        }

        return received;
    }

    private static PublishedSeatEvent ToPublishedEvent(Message message)
    {
        string? eventType = null;
        if (message.MessageAttributes != null && message.MessageAttributes.ContainsKey("eventType"))
        {
            eventType = message.MessageAttributes["eventType"].StringValue;
        }

        using var document = JsonDocument.Parse(message.Body);
        return new PublishedSeatEvent(eventType, document.RootElement.Clone());
    }

    private IConfiguration Configuration(string inventoryTopicArn)
    {
        var values = new Dictionary<string, string?>
        {
            { "SeatStoreTableName", TableName },
            { "InventoryEventsTopicArn", inventoryTopicArn },
            { "AuditEventsTopicArn", _auditTopicArn },
        };
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
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
            StreamSpecification = new StreamSpecification { StreamEnabled = true, StreamViewType = StreamViewType.NEW_AND_OLD_IMAGES },
        };
        await dynamoDb.CreateTableAsync(request);
    }
}
