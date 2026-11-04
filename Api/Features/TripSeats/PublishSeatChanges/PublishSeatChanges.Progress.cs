namespace Api.Features.TripSeats.PublishSeatChanges;

/// <summary>
/// Keeps the publisher's own state in the seat store: the lease that lets one running copy of the
/// API publish at a time, and how far that copy has published each shard of the change stream.
/// </summary>
public sealed class SeatPublisherProgress
{
    /// <summary>The partition of the publisher's own items; no trip's key can start with it.</summary>
    public const string PartitionKey = "PUBLISHER#SeatChanges";

    private const string LeaseSortKey = "LEASE";
    private const string ShardSortKeyPrefix = "SHARD#";

    // Renewed on every run (every few seconds); a copy that stops renewing — stopped, crashed, cut off
    // from the table — loses the lease this long after its last run, and another copy takes over.
    private static readonly TimeSpan _leaseLength = TimeSpan.FromSeconds(30);

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;
    private readonly IClock _clock;

    // One name per running copy, so the lease tells copies apart.
    private readonly string _owner = Guid.NewGuid().ToString("D");

    public SeatPublisherProgress(IAmazonDynamoDB dynamoDb, IConfiguration configuration, IClock clock)
    {
        _dynamoDb = dynamoDb;
        // Present by the time the publisher runs: startup refuses to run without it.
        _tableName = configuration[Infrastructure.ConfigKeys.SeatStoreTableName]!;
        _clock = clock;
    }

    /// <summary>
    /// Takes the lease, or renews it when this copy already holds it. False while another copy holds
    /// an unexpired lease: two copies publishing at once could publish one seat's events out of order.
    /// </summary>
    public async Task<bool> TryTakeLeaseAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;
        var request = new PutItemRequest
        {
            TableName = _tableName,
            Item = new Dictionary<string, AttributeValue>
            {
                { "pk", new AttributeValue { S = PartitionKey } },
                { "sk", new AttributeValue { S = LeaseSortKey } },
                { "leaseOwner", new AttributeValue { S = _owner } },
                { "leaseExpiresAt", new AttributeValue { S = ToText(now + _leaseLength) } },
            },
            // ISO-8601 UTC text of one fixed width sorts in time order, so a text comparison is a time comparison.
            ConditionExpression = "attribute_not_exists(pk) OR leaseOwner = :owner OR leaseExpiresAt < :now",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":owner", new AttributeValue { S = _owner } },
                { ":now", new AttributeValue { S = ToText(now) } },
            },
        };
        try
        {
            await _dynamoDb.PutItemAsync(request, cancellationToken);
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }

        return true;
    }

    /// <summary>How far each shard has been published, by shard identifier.</summary>
    public async Task<Dictionary<string, ShardProgress>> ListShardsAsync(CancellationToken cancellationToken)
    {
        var query = new QueryRequest
        {
            TableName = _tableName,
            KeyConditionExpression = "pk = :pk AND begins_with(sk, :shard)",
            ConsistentRead = true,
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":pk", new AttributeValue { S = PartitionKey } },
                { ":shard", new AttributeValue { S = ShardSortKeyPrefix } },
            },
        };

        var shards = new Dictionary<string, ShardProgress>(StringComparer.Ordinal);
        do
        {
            QueryResponse page = await _dynamoDb.QueryAsync(query, cancellationToken);
            if (page.Items != null)
            {
                AddShards(shards, page.Items);
            }

            query.ExclusiveStartKey = page.LastEvaluatedKey;
        }
        while (query.ExclusiveStartKey != null && query.ExclusiveStartKey.Count > 0);

        return shards;
    }

    /// <summary>Records how far a shard has been published.</summary>
    public async Task SaveShardAsync(ShardProgress progress, CancellationToken cancellationToken)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            { "pk", new AttributeValue { S = PartitionKey } },
            { "sk", new AttributeValue { S = ShardSortKeyPrefix + progress.ShardId } },
            { "shardId", new AttributeValue { S = progress.ShardId } },
            { "isFinished", new AttributeValue { BOOL = progress.IsFinished } },
            { "savedAt", new AttributeValue { S = ToText(progress.SavedAt) } },
        };
        if (progress.SequenceNumber != null)
        {
            item["sequenceNumber"] = new AttributeValue { S = progress.SequenceNumber };
        }

        if (progress.AlertedRequest != null)
        {
            item["alertedRequest"] = new AttributeValue { S = progress.AlertedRequest };
        }

        await _dynamoDb.PutItemAsync(new PutItemRequest { TableName = _tableName, Item = item }, cancellationToken);
    }

    private static void AddShards(Dictionary<string, ShardProgress> shards, List<Dictionary<string, AttributeValue>> items)
    {
        foreach (Dictionary<string, AttributeValue> item in items)
        {
            ShardProgress progress = ToProgress(item);
            shards[progress.ShardId] = progress;
        }
    }

    private static ShardProgress ToProgress(Dictionary<string, AttributeValue> item)
    {
        var progress = new ShardProgress
        {
            ShardId = item["shardId"].S,
            IsFinished = item["isFinished"].BOOL == true,
            SavedAt = DateTimeOffset.Parse(item["savedAt"].S, System.Globalization.CultureInfo.InvariantCulture),
        };
        if (item.ContainsKey("sequenceNumber"))
        {
            progress = progress with { SequenceNumber = item["sequenceNumber"].S };
        }

        if (item.ContainsKey("alertedRequest"))
        {
            progress = progress with { AlertedRequest = item["alertedRequest"].S };
        }

        return progress;
    }

    private static string ToText(DateTimeOffset instant)
    {
        return instant.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>How far one shard of the change stream has been published.</summary>
public sealed record ShardProgress
{
    public required string ShardId { get; init; }

    /// <summary>The last record handled; every record up to it is published (or announces nothing).</summary>
    public string? SequenceNumber { get; init; }

    /// <summary>True once the shard is closed and every record on it is handled.</summary>
    public required bool IsFinished { get; init; }

    /// <summary>When this progress was last saved.</summary>
    public required DateTimeOffset SavedAt { get; init; }

    /// <summary>The request whose delay has already been alerted, so the alert is raised once, not every run.</summary>
    public string? AlertedRequest { get; init; }
}
