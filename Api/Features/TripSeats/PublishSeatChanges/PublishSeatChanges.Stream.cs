using Amazon.DynamoDBStreams;
using Amazon.DynamoDBStreams.Model;
using StoreAttribute = Amazon.DynamoDBv2.Model.AttributeValue;
using StreamAttribute = Amazon.DynamoDBStreams.Model.AttributeValue;

namespace Api.Features.TripSeats.PublishSeatChanges;

/// <summary>
/// Reads what the seat store recorded: the shards of its change stream, the changes on one shard
/// after a given point, and the operators of trips — turned into plain records, so nothing past this
/// class touches the stream's own types.
/// </summary>
public sealed class SeatChangeStream
{
    private const string TripKeyPrefix = "TRIP#";
    private const string TripSortKey = "TRIP";
    private const string SeatSortKeyPrefix = "SEAT#";
    private const string ModifyEvent = "MODIFY";
    private const string RequiredViewType = "NEW_AND_OLD_IMAGES";
    private const int PageSize = 1000;
    private const int MaxKeysPerBatchGet = 100;

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly IAmazonDynamoDBStreams _streams;
    private readonly string _tableName;

    public SeatChangeStream(IAmazonDynamoDB dynamoDb, IAmazonDynamoDBStreams streams, IConfiguration configuration)
    {
        _dynamoDb = dynamoDb;
        _streams = streams;
        // Present by the time the publisher runs: startup refuses to run without it.
        _tableName = configuration[Infrastructure.ConfigKeys.SeatStoreTableName]!;
    }

    /// <summary>Every shard the stream still holds, parents listed before their children.</summary>
    public async Task<List<ChangeShard>> ListShardsAsync(CancellationToken cancellationToken)
    {
        string streamArn = await GetStreamArnAsync(cancellationToken);
        var shards = new List<ChangeShard>();
        string? startAfter = null;
        do
        {
            var request = new DescribeStreamRequest { StreamArn = streamArn, ExclusiveStartShardId = startAfter };
            DescribeStreamResponse response = await _streams.DescribeStreamAsync(request, cancellationToken);
            StreamDescription description = response.StreamDescription;
            if (description.Shards != null)
            {
                List<ChangeShard> page = description.Shards.ConvertAll(shard => new ChangeShard(streamArn, shard.ShardId, shard.ParentShardId));
                shards.AddRange(page);
            }

            startAfter = description.LastEvaluatedShardId;
        }
        while (startAfter != null);

        return shards;
    }

    /// <summary>
    /// Everything recorded on a shard after the given point (from its start when there is none), up to
    /// what is there now, and whether the shard is closed — a closed shard gets no more changes.
    /// </summary>
    public async Task<ShardRead> ReadAsync(ChangeShard shard, string? afterSequenceNumber, CancellationToken cancellationToken)
    {
        string? iterator = await GetIteratorAsync(shard, afterSequenceNumber, cancellationToken);
        var entries = new List<StreamEntry>();
        while (iterator != null)
        {
            var request = new GetRecordsRequest { ShardIterator = iterator, Limit = PageSize };
            GetRecordsResponse page = await _streams.GetRecordsAsync(request, cancellationToken);
            iterator = page.NextShardIterator;
            if (page.Records is null || page.Records.Count == 0)
            {
                break;
            }

            List<StreamEntry> pageEntries = page.Records.ConvertAll(ToEntry);
            entries.AddRange(pageEntries);
        }

        return new ShardRead(entries, iterator is null);
    }

    /// <summary>The operator of each trip asked about, by trip identifier; a trip with no trip item is left out.</summary>
    public async Task<Dictionary<string, string>> ListTripOperatorsAsync(List<string> tripIds, CancellationToken cancellationToken)
    {
        var operators = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string[] chunk in tripIds.Chunk(MaxKeysPerBatchGet))
        {
            List<Dictionary<string, StoreAttribute>> trips = await ListTripItemsAsync(chunk, cancellationToken);
            AddOperators(operators, trips);
        }

        return operators;
    }

    private static void AddOperators(Dictionary<string, string> operators, List<Dictionary<string, StoreAttribute>> trips)
    {
        foreach (Dictionary<string, StoreAttribute> trip in trips)
        {
            if (trip.ContainsKey("operatorId"))
            {
                string tripId = trip["pk"].S.Substring(TripKeyPrefix.Length);
                operators[tripId] = trip["operatorId"].S;
            }
        }
    }

    private async Task<List<Dictionary<string, StoreAttribute>>> ListTripItemsAsync(
        string[] tripIds, CancellationToken cancellationToken)
    {
        var request = new BatchGetItemRequest
        {
            RequestItems = new Dictionary<string, KeysAndAttributes>
            {
                { _tableName, new KeysAndAttributes { Keys = TripKeys(tripIds), ProjectionExpression = "pk, operatorId" } },
            },
        };
        var items = new List<Dictionary<string, StoreAttribute>>();
        // A batch read may hand back some keys unread when the table is busy; they are asked for again.
        do
        {
            BatchGetItemResponse response = await _dynamoDb.BatchGetItemAsync(request, cancellationToken);
            if (response.Responses != null && response.Responses.ContainsKey(_tableName))
            {
                items.AddRange(response.Responses[_tableName]);
            }

            request.RequestItems = response.UnprocessedKeys;
        }
        while (request.RequestItems != null && request.RequestItems.Count > 0);

        return items;
    }

    private static List<Dictionary<string, StoreAttribute>> TripKeys(string[] tripIds)
    {
        var keys = new List<Dictionary<string, StoreAttribute>>();
        foreach (string tripId in tripIds)
        {
            keys.Add(new Dictionary<string, StoreAttribute>
            {
                { "pk", new StoreAttribute { S = TripKeyPrefix + tripId } },
                { "sk", new StoreAttribute { S = TripSortKey } },
            });
        }

        return keys;
    }

    // Read on every run rather than once: turning the stream off and on again gives it a new identity.
    private async Task<string> GetStreamArnAsync(CancellationToken cancellationToken)
    {
        DescribeTableResponse response = await _dynamoDb.DescribeTableAsync(_tableName, cancellationToken);
        TableDescription table = response.Table;
        bool hasStream = !string.IsNullOrEmpty(table.LatestStreamArn) && table.StreamSpecification != null;
        if (!hasStream || table.StreamSpecification!.StreamViewType != RequiredViewType)
        {
            throw new InvalidOperationException(
                $"The seat store table '{_tableName}' has no change stream showing old and new seats. Turn it on: "
                + $"aws dynamodb update-table --table-name {_tableName} "
                + $"--stream-specification StreamEnabled=true,StreamViewType={RequiredViewType} (locally: scripts/local-setup.sh).");
        }

        return table.LatestStreamArn;
    }

    private async Task<string?> GetIteratorAsync(ChangeShard shard, string? afterSequenceNumber, CancellationToken cancellationToken)
    {
        var request = new GetShardIteratorRequest
        {
            StreamArn = shard.StreamArn,
            ShardId = shard.ShardId,
            ShardIteratorType = ShardIteratorType.TRIM_HORIZON,
        };
        if (afterSequenceNumber != null)
        {
            request.ShardIteratorType = ShardIteratorType.AFTER_SEQUENCE_NUMBER;
            request.SequenceNumber = afterSequenceNumber;
        }

        GetShardIteratorResponse response = await _streams.GetShardIteratorAsync(request, cancellationToken);
        return response.ShardIterator;
    }

    private static StreamEntry ToEntry(Amazon.DynamoDBStreams.Model.Record record)
    {
        StreamRecord change = record.Dynamodb;
        string partitionKey = Text(change.Keys, "pk")!;
        string sortKey = Text(change.Keys, "sk")!;
        var entry = new StreamEntry
        {
            SequenceNumber = change.SequenceNumber,
            RecordedAt = ToUtc(change.ApproximateCreationDateTime),
            IsPublisherItem = partitionKey.StartsWith(SeatPublisherProgress.PartitionKey, StringComparison.Ordinal),
        };

        // A seat being created or removed announces nothing; only a change to an existing seat does.
        bool isSeatChange = sortKey.StartsWith(SeatSortKeyPrefix, StringComparison.Ordinal) && record.EventName == ModifyEvent;
        if (!isSeatChange)
        {
            return entry;
        }

        return entry with { Change = ToSeatChange(partitionKey, sortKey, change.NewImage, change.OldImage) };
    }

    private static SeatChange ToSeatChange(
        string partitionKey, string sortKey, Dictionary<string, StreamAttribute> after, Dictionary<string, StreamAttribute> before)
    {
        return new SeatChange
        {
            TripId = partitionKey.Substring(TripKeyPrefix.Length),
            SeatNo = sortKey.Substring(SeatSortKeyPrefix.Length),
            Status = Text(after, "status"),
            Reason = Text(after, "lastChangeReason"),
            Request = Text(after, "lastChangeRequest"),
            CorrelationId = Text(after, "lastChangeCorrelationId"),
            ChangedAt = Text(after, "lastChangedAt"),
            BookingId = Text(after, "bookingId"),
            PreviousBookingId = Text(before, "bookingId"),
            HoldExpiresAt = Text(after, "holdExpiresAt"),
            ChangedBy = Text(after, "lastChangedBy"),
            Note = Text(after, "lastChangeNote"),
            MovedFrom = Text(after, "lastChangeMovedFrom"),
        };
    }

    private static string? Text(Dictionary<string, StreamAttribute>? image, string name)
    {
        if (image is null || !image.ContainsKey(name))
        {
            return null;
        }

        return image[name].S;
    }

    private static DateTimeOffset ToUtc(DateTime? approximateCreation)
    {
        DateTime recordedAt = approximateCreation.GetValueOrDefault();
        return new DateTimeOffset(DateTime.SpecifyKind(recordedAt, DateTimeKind.Utc));
    }
}

/// <summary>One shard of the seat store's change stream.</summary>
/// <param name="StreamArn">The stream the shard belongs to.</param>
/// <param name="ShardId">The shard.</param>
/// <param name="ParentShardId">The shard it split from, whose changes come first; null for none.</param>
public sealed record ChangeShard(string StreamArn, string ShardId, string? ParentShardId);

/// <summary>What one read of a shard found.</summary>
/// <param name="Entries">Every record read, in the order the stream holds them.</param>
/// <param name="IsClosed">True when the shard is closed and everything on it has been read.</param>
public sealed record ShardRead(List<StreamEntry> Entries, bool IsClosed);

/// <summary>One record of the stream: where it sits, when it was made, and the seat change it carries, if any.</summary>
public sealed record StreamEntry
{
    /// <summary>The record's position on its shard.</summary>
    public required string SequenceNumber { get; init; }

    /// <summary>About when the change was made (to the second).</summary>
    public required DateTimeOffset RecordedAt { get; init; }

    /// <summary>True for the publisher's own lease and progress items, which say nothing about seats.</summary>
    public required bool IsPublisherItem { get; init; }

    /// <summary>The change to an existing seat, or null when the record is about anything else.</summary>
    public SeatChange? Change { get; init; }
}

/// <summary>
/// One seat's change as the request that made it recorded it on the seat. Every value is as stored,
/// and may be missing; deciding what a missing value means is left to whoever names the event.
/// </summary>
public sealed record SeatChange
{
    public required string TripId { get; init; }

    public required string SeatNo { get; init; }

    /// <summary>The seat's status after the change.</summary>
    public string? Status { get; init; }

    /// <summary>Why the seat changed (Held, Booked, HoldReleased, OperatorBlocked, …).</summary>
    public string? Reason { get; init; }

    /// <summary>The request that made the change; every seat it changed shares it.</summary>
    public string? Request { get; init; }

    public string? CorrelationId { get; init; }

    /// <summary>When the change was made, ISO-8601 UTC.</summary>
    public string? ChangedAt { get; init; }

    /// <summary>The booking on the seat after the change.</summary>
    public string? BookingId { get; init; }

    /// <summary>The booking on the seat before the change — the one a release freed.</summary>
    public string? PreviousBookingId { get; init; }

    public string? HoldExpiresAt { get; init; }

    /// <summary>The staff user who made the change, for staff actions.</summary>
    public string? ChangedBy { get; init; }

    /// <summary>The admin's note, for an admin hold cancellation.</summary>
    public string? Note { get; init; }

    /// <summary>For a seat change, the seat this one replaced.</summary>
    public string? MovedFrom { get; init; }
}
