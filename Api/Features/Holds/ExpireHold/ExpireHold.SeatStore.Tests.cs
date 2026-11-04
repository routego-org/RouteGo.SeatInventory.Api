namespace Api.Features.Holds.ExpireHold;

/// <summary>A clock that always reads the same instant, so stored change times are exact.</summary>
public sealed class ExpireHoldFixedClock : IClock
{
    private readonly DateTimeOffset _utcNow;

    public ExpireHoldFixedClock(DateTimeOffset utcNow)
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
/// DynamoDB Local with the seat store table, and LocalStack with the hold-expiry queue. The handler and
/// the queue worker are driven directly; seats are seeded and read back raw, because the change record
/// on each freed seat is what the seat-change stream turns into SeatReleased.
/// </summary>
public sealed class ExpireHoldSeatStore : IAsyncLifetime
{
    /// <summary>The instant the worker believes it is.</summary>
    public static readonly DateTimeOffset Now = new DateTimeOffset(2026, 10, 24, 10, 10, 0, TimeSpan.Zero);

    /// <summary>The hold expiry the seats and their timer carry, exactly as CreateHold stores it.</summary>
    public const string HoldExpiresAt = "2026-10-24T10:10:00.0000000Z";

    private const string TableName = "hold-expiry-tests";

    private readonly DynamoDbContainer _dynamoDbContainer = new DynamoDbBuilder("amazon/dynamodb-local:2.6.1").Build();
    private readonly LocalStackContainer _localStack = new LocalStackBuilder("localstack/localstack:4.4").Build();
    private AmazonDynamoDBClient? _dynamoDb;
    private AmazonSQSClient? _sqs;

    public ExpireHoldHandler? Handler { get; private set; }

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_dynamoDbContainer.StartAsync(), _localStack.StartAsync());
        var credentials = new Amazon.Runtime.BasicAWSCredentials("local", "local");
        _dynamoDb = new AmazonDynamoDBClient(credentials, new AmazonDynamoDBConfig { ServiceURL = _dynamoDbContainer.GetConnectionString() });
        _sqs = new AmazonSQSClient(credentials, new AmazonSQSConfig { ServiceURL = _localStack.GetConnectionString() });
        await CreateTableAsync(_dynamoDb);
        Handler = new ExpireHoldHandler(_dynamoDb, Configuration(TableName, null), new ExpireHoldFixedClock(Now));
    }

    public async Task DisposeAsync()
    {
        _dynamoDb?.Dispose();
        _sqs?.Dispose();
        await Task.WhenAll(_dynamoDbContainer.DisposeAsync().AsTask(), _localStack.DisposeAsync().AsTask());
    }

    /// <summary>A queue of its own for one test, so no test's timers reach another.</summary>
    public async Task<string> CreateQueueAsync(string queueName)
    {
        CreateQueueResponse queue = await _sqs!.CreateQueueAsync(queueName);
        return queue.QueueUrl;
    }

    /// <summary>The queue worker reading the given queue and freeing seats in the seat store.</summary>
    public ExpireHoldService WorkerOn(string queueUrl)
    {
        return WorkerFor(TableName, queueUrl);
    }

    /// <summary>A queue worker whose seat store is unavailable: its table does not exist.</summary>
    public ExpireHoldService WorkerWithSeatStoreDownOn(string queueUrl)
    {
        return WorkerFor("no-such-table", queueUrl);
    }

    public async Task PutTripAsync(Guid tripId, bool isCancelled)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            { "pk", new AttributeValue { S = "TRIP#" + tripId.ToString("D") } },
            { "sk", new AttributeValue { S = "TRIP" } },
            { "departureAt", new AttributeValue { S = "2026-10-30T10:00:00.0000000Z" } },
            { "isCancelled", new AttributeValue { BOOL = isCancelled } },
        };
        await _dynamoDb!.PutItemAsync(new PutItemRequest { TableName = TableName, Item = item });
    }

    /// <summary>Stores a seat Held by a booking until the given expiry, as a hold leaves it.</summary>
    public async Task PutHeldSeatAsync(Guid tripId, string seatNo, Guid bookingId, string holdExpiresAt)
    {
        Dictionary<string, AttributeValue> item = Seat(tripId, seatNo, "Held");
        item["bookingId"] = new AttributeValue { S = bookingId.ToString("D") };
        item["holdExpiresAt"] = new AttributeValue { S = holdExpiresAt };
        await _dynamoDb!.PutItemAsync(new PutItemRequest { TableName = TableName, Item = item });
    }

    /// <summary>Stores a seat Booked by a booking, as a confirm leaves it.</summary>
    public async Task PutBookedSeatAsync(Guid tripId, string seatNo, Guid bookingId)
    {
        Dictionary<string, AttributeValue> item = Seat(tripId, seatNo, "Booked");
        item["bookingId"] = new AttributeValue { S = bookingId.ToString("D") };
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

    /// <summary>Puts a timer on the queue, due at once, as CreateHold's would be at the hold expiry.</summary>
    public async Task SendTimerAsync(string queueUrl, HoldExpiryTimerMessage timer)
    {
        string body = JsonSerializer.Serialize(timer, WireJson.Options);
        await _sqs!.SendMessageAsync(new SendMessageRequest { QueueUrl = queueUrl, MessageBody = body });
    }

    /// <summary>How many timers are still on the queue, waiting or being retried.</summary>
    public async Task<int> CountQueuedTimersAsync(string queueUrl)
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

    private ExpireHoldService WorkerFor(string tableName, string queueUrl)
    {
        IConfiguration configuration = Configuration(tableName, queueUrl);
        var handler = new ExpireHoldHandler(_dynamoDb!, configuration, new ExpireHoldFixedClock(Now));
        Microsoft.Extensions.Logging.ILogger<ExpireHoldService> logger =
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ExpireHoldService>.Instance;
        return new ExpireHoldService(handler, _sqs!, configuration, logger);
    }

    private static IConfiguration Configuration(string tableName, string? queueUrl)
    {
        var values = new Dictionary<string, string?>
        {
            { "SeatStoreTableName", tableName },
            { "HoldExpiryQueueUrl", queueUrl },
        };
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static Dictionary<string, AttributeValue> Seat(Guid tripId, string seatNo, string status)
    {
        return new Dictionary<string, AttributeValue>
        {
            { "pk", new AttributeValue { S = "TRIP#" + tripId.ToString("D") } },
            { "sk", new AttributeValue { S = "SEAT#" + seatNo } },
            { "seatNo", new AttributeValue { S = seatNo } },
            { "status", new AttributeValue { S = status } },
            { "lastChangeReason", new AttributeValue { S = "Held" } },
            { "lastChangeRequest", new AttributeValue { S = "seed" } },
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
