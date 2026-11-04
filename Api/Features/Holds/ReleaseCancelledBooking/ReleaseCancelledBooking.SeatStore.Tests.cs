namespace Api.Features.Holds.ReleaseCancelledBooking;

/// <summary>A clock that always reads the same instant, so stored change times are exact.</summary>
public sealed class ReleaseCancelledBookingFixedClock : IClock
{
    private readonly DateTimeOffset _utcNow;

    public ReleaseCancelledBookingFixedClock(DateTimeOffset utcNow)
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
/// DynamoDB Local with the seat store table. The handler is driven directly; trips and seats are seeded
/// and read back raw, because the change record on each freed seat is what the seat-change stream turns
/// into SeatReleased.
/// </summary>
public sealed class ReleaseCancelledBookingSeatStore : IAsyncLifetime
{
    /// <summary>The instant the worker believes it is.</summary>
    public static readonly DateTimeOffset Now = new DateTimeOffset(2026, 10, 22, 11, 0, 0, TimeSpan.Zero);

    private const string TableName = "booking-cancelled-tests";

    private readonly DynamoDbContainer _container = new DynamoDbBuilder("amazon/dynamodb-local:2.6.1").Build();
    private AmazonDynamoDBClient? _dynamoDb;

    public ReleaseCancelledBookingHandler? Handler { get; private set; }

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        // The same region every test class uses: DynamoDB Local keeps tables per region.
        Environment.SetEnvironmentVariable("AWS_REGION", "ap-south-1");
        var credentials = new Amazon.Runtime.BasicAWSCredentials("local", "local");
        _dynamoDb = new AmazonDynamoDBClient(credentials, new AmazonDynamoDBConfig { ServiceURL = _container.GetConnectionString() });
        await CreateTableAsync(_dynamoDb);
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "SeatStoreTableName", TableName } })
            .Build();
        Microsoft.Extensions.Logging.ILogger<ReleaseCancelledBookingHandler> logger =
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ReleaseCancelledBookingHandler>.Instance;
        Handler = new ReleaseCancelledBookingHandler(_dynamoDb, configuration, new ReleaseCancelledBookingFixedClock(Now), logger);
    }

    public async Task DisposeAsync()
    {
        _dynamoDb?.Dispose();
        await _container.DisposeAsync();
    }

    /// <summary>A BookingCancelled event for the booking on the trip, listing the given seats.</summary>
    public static ReceivedEvent Event(Guid bookingId, Guid tripId, params string[] seatNos)
    {
        var data = new Dictionary<string, object?>
        {
            { "bookingId", bookingId },
            { "tripId", tripId },
            { "seatNos", seatNos },
            { "refundAmount", 900 },
            { "reason", "CustomerRequest" },
        };
        return EventWith(data);
    }

    /// <summary>A BookingCancelled event with exactly the given data.</summary>
    public static ReceivedEvent EventWith(Dictionary<string, object?> data)
    {
        return new ReceivedEvent
        {
            EventId = "BC1",
            EventType = "BookingCancelled",
            OccurredAt = Now,
            CorrelationId = "booking-correlation",
            Data = JsonSerializer.SerializeToElement(data),
        };
    }

    public async Task PutTripAsync(Guid tripId, bool isCancelled)
    {
        Dictionary<string, AttributeValue> item = Key(tripId, "TRIP");
        item["departureAt"] = new AttributeValue { S = "2026-10-24T16:30:00.0000000Z" };
        item["isCancelled"] = new AttributeValue { BOOL = isCancelled };
        await _dynamoDb!.PutItemAsync(new PutItemRequest { TableName = TableName, Item = item });
    }

    /// <summary>Stores a seat Held or Booked by the booking.</summary>
    public async Task PutOwnedSeatAsync(Guid tripId, string seatNo, string status, Guid bookingId)
    {
        Dictionary<string, AttributeValue> item = Key(tripId, "SEAT#" + seatNo);
        item["seatNo"] = new AttributeValue { S = seatNo };
        item["status"] = new AttributeValue { S = status };
        item["bookingId"] = new AttributeValue { S = bookingId.ToString("D") };
        item["lastChangeReason"] = new AttributeValue { S = status };
        item["lastChangeRequest"] = new AttributeValue { S = "seed" };
        if (status == "Held")
        {
            item["holdExpiresAt"] = new AttributeValue { S = "2026-10-22T11:10:00.0000000Z" };
        }

        await _dynamoDb!.PutItemAsync(new PutItemRequest { TableName = TableName, Item = item });
    }

    /// <summary>One seat's stored attributes, as plain strings (absent attributes are missing keys).</summary>
    public async Task<Dictionary<string, string>> GetStoredSeatAsync(Guid tripId, string seatNo)
    {
        GetItemResponse response = await _dynamoDb!.GetItemAsync(
            new GetItemRequest { TableName = TableName, Key = Key(tripId, "SEAT#" + seatNo), ConsistentRead = true });
        var seat = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, AttributeValue> attribute in response.Item)
        {
            seat[attribute.Key] = attribute.Value.S ?? attribute.Value.N;
        }

        return seat;
    }

    private static Dictionary<string, AttributeValue> Key(Guid tripId, string sortKey)
    {
        return new Dictionary<string, AttributeValue>
        {
            { "pk", new AttributeValue { S = "TRIP#" + tripId.ToString("D") } },
            { "sk", new AttributeValue { S = sortKey } },
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
