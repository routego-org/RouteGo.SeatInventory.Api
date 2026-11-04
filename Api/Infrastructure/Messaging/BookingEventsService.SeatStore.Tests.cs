using Api.Features.Holds.ReleaseCancelledBooking;
using Api.Features.Holds.ReleaseFailedBooking;

namespace Api.Infrastructure.Messaging;

/// <summary>One test's own queue and its dead-letter queue.</summary>
/// <param name="QueueUrl">Where the test puts its events.</param>
/// <param name="DeadLetterQueueUrl">Where an event that can never be applied is set aside.</param>
public sealed record BookingEventsQueues(string QueueUrl, string DeadLetterQueueUrl);

/// <summary>
/// DynamoDB Local with the seat store table, and LocalStack for the booking queues. The booking queue
/// worker is driven directly with both slices' real handlers; seats are seeded and read back raw.
/// </summary>
public sealed class BookingEventsSeatStore : IAsyncLifetime
{
    private const string TableName = "booking-events-tests";

    private readonly DynamoDbContainer _dynamoDbContainer = new DynamoDbBuilder("amazon/dynamodb-local:2.6.1").Build();
    private readonly LocalStackContainer _localStack = new LocalStackBuilder("localstack/localstack:4.4").Build();
    private AmazonDynamoDBClient? _dynamoDb;
    private AmazonSQSClient? _sqs;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_dynamoDbContainer.StartAsync(), _localStack.StartAsync());
        // The same region every test class uses: DynamoDB Local keeps tables per region.
        Environment.SetEnvironmentVariable("AWS_REGION", "ap-south-1");
        var credentials = new Amazon.Runtime.BasicAWSCredentials("local", "local");
        _dynamoDb = new AmazonDynamoDBClient(credentials, new AmazonDynamoDBConfig { ServiceURL = _dynamoDbContainer.GetConnectionString() });
        _sqs = new AmazonSQSClient(credentials, new AmazonSQSConfig { ServiceURL = _localStack.GetConnectionString() });
        await CreateTableAsync(_dynamoDb);
    }

    public async Task DisposeAsync()
    {
        _dynamoDb?.Dispose();
        _sqs?.Dispose();
        await Task.WhenAll(_dynamoDbContainer.DisposeAsync().AsTask(), _localStack.DisposeAsync().AsTask());
    }

    /// <summary>A Booking event of the given type for the booking on the trip.</summary>
    public static ReceivedEvent Event(string eventType, Guid bookingId, Guid tripId)
    {
        var data = new Dictionary<string, object?> { { "bookingId", bookingId }, { "tripId", tripId } };
        return EventWith(eventType, data);
    }

    /// <summary>A Booking event of the given type with exactly the given data.</summary>
    public static ReceivedEvent EventWith(string eventType, Dictionary<string, object?> data)
    {
        return new ReceivedEvent
        {
            EventId = "B-" + eventType,
            EventType = eventType,
            OccurredAt = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero),
            CorrelationId = "booking-correlation",
            Data = JsonSerializer.SerializeToElement(data),
        };
    }

    public async Task PutTripAsync(Guid tripId)
    {
        Dictionary<string, AttributeValue> item = Key(tripId, "TRIP");
        item["departureAt"] = new AttributeValue { S = "2026-10-24T16:30:00.0000000Z" };
        item["isCancelled"] = new AttributeValue { BOOL = false };
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
        await _dynamoDb!.PutItemAsync(new PutItemRequest { TableName = TableName, Item = item });
    }

    /// <summary>The seat's stored status.</summary>
    public async Task<string> GetSeatStatusAsync(Guid tripId, string seatNo)
    {
        GetItemResponse response = await _dynamoDb!.GetItemAsync(
            new GetItemRequest { TableName = TableName, Key = Key(tripId, "SEAT#" + seatNo), ConsistentRead = true });
        return response.Item["status"].S;
    }

    /// <summary>A queue and its dead-letter queue of their own for one test, so no test's events reach another.</summary>
    public async Task<BookingEventsQueues> CreateQueuesAsync(string queueName)
    {
        CreateQueueResponse queue = await _sqs!.CreateQueueAsync(queueName);
        CreateQueueResponse deadLetterQueue = await _sqs.CreateQueueAsync(queueName + "-dlq");
        return new BookingEventsQueues(queue.QueueUrl, deadLetterQueue.QueueUrl);
    }

    /// <summary>The booking queue worker reading the given queues and freeing seats in the seat store.</summary>
    public BookingEventsService WorkerOn(BookingEventsQueues queues)
    {
        return WorkerFor(TableName, queues);
    }

    /// <summary>A booking queue worker whose seat store is unavailable: its table does not exist.</summary>
    public BookingEventsService WorkerWithSeatStoreDownOn(BookingEventsQueues queues)
    {
        return WorkerFor("no-such-table", queues);
    }

    /// <summary>Puts an event on the queue in the RouteGo envelope, as the Booking subscription delivers it.</summary>
    public async Task SendEventAsync(string queueUrl, ReceivedEvent receivedEvent)
    {
        var envelope = new Dictionary<string, object?>
        {
            { "eventId", receivedEvent.EventId },
            { "eventType", receivedEvent.EventType },
            { "version", 1 },
            { "occurredAt", receivedEvent.OccurredAt },
            { "correlationId", receivedEvent.CorrelationId },
            { "producer", "booking-service" },
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

    private BookingEventsService WorkerFor(string tableName, BookingEventsQueues queues)
    {
        var values = new Dictionary<string, string?>
        {
            { "SeatStoreTableName", tableName },
            { "BookingEventsQueueUrl", queues.QueueUrl },
            { "BookingEventsDeadLetterQueueUrl", queues.DeadLetterQueueUrl },
        };
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var clock = new SystemClock();
        var releaseFailed = new ReleaseFailedBookingHandler(
            _dynamoDb!, configuration, clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<ReleaseFailedBookingHandler>.Instance);
        var releaseCancelled = new ReleaseCancelledBookingHandler(
            _dynamoDb!, configuration, clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<ReleaseCancelledBookingHandler>.Instance);
        var dispatcher = new BookingEventDispatcher(releaseFailed, releaseCancelled);
        return new BookingEventsService(
            dispatcher, _sqs!, configuration, Microsoft.Extensions.Logging.Abstractions.NullLogger<BookingEventsService>.Instance);
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
