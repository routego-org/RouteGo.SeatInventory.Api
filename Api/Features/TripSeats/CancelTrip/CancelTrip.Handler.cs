namespace Api.Features.TripSeats.CancelTrip;

/// <summary>
/// Marks a trip Fleet cancelled as cancelled, for good, and blocks every seat that is Available or
/// blocked by the operator with block reason TripCancelled — together, in one DynamoDB transaction.
/// Held and Booked seats keep their booking until Booking releases them, and are blocked then. A
/// cancellation for a trip not known yet is remembered as a cancelled trip with no seats, so its seats
/// are created Blocked. Repeat-safe by the trip's cancelled mark: a trip already cancelled changes
/// nothing, and the write itself is conditional on the trip still being open. Every blocked seat records
/// change reason TripCancelled, which the seat-change stream does not announce.
/// </summary>
public sealed class CancelTripHandler
{
    /// <summary>The Fleet event type that starts this worker.</summary>
    public const string TriggerEventType = "TripCancelled";

    private const string TripKeyPrefix = "TRIP#";
    private const string TripSortKey = "TRIP";
    private const string SeatSortKeyPrefix = "SEAT#";
    private const string ChangeReason = "TripCancelled";
    private const string ConditionFailed = "ConditionalCheckFailed";
    private const string TransactionConflict = "TransactionConflict";

    // A cancellation can lose a race with a hold, a release, an operator block or the trip's own
    // creation; a few attempts, each after a fresh read, settle it on what the seats are now.
    private const int MaxWriteAttempts = 3;

    // STATUS is a DynamoDB reserved word, hence the placeholder.
    private const string Projection = "sk, isCancelled, seatsCreatedAt, seatNo, #status, blockReason";

    private const string StillOpen = "attribute_not_exists(isCancelled) OR isCancelled = :false";

    // With no seats read, none may appear before the write: they would be created Available.
    private const string StillOpenWithoutSeats = "(attribute_not_exists(isCancelled) OR isCancelled = :false) AND attribute_not_exists(seatsCreatedAt)";

    private const string BlockForCancellation =
        "SET #status = :blocked, blockReason = :tripCancelled, lastChangeReason = :reason, lastChangeRequest = :request, "
        + "lastChangeCorrelationId = :correlation, lastChangedAt = :now "
        + "REMOVE lastChangedBy, lastChangeNote, lastChangeMovedFrom";

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;
    private readonly IClock _clock;
    private readonly ILogger<CancelTripHandler> _logger;

    public CancelTripHandler(IAmazonDynamoDB dynamoDb, IConfiguration configuration, IClock clock, ILogger<CancelTripHandler> logger)
    {
        _dynamoDb = dynamoDb;
        // Present by the time any event arrives: startup refuses to run without it.
        _tableName = configuration[Infrastructure.ConfigKeys.SeatStoreTableName]!;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Applies one TripCancelled event and says what should happen to its queue message.</summary>
    public async Task<EventOutcome> HandleAsync(ReceivedEvent receivedEvent, CancellationToken cancellationToken)
    {
        TripCancelledMessage? message = ReadMessage(receivedEvent.Data);
        if (message is null || message.TripId is null)
        {
            return EventOutcome.Rejected("tripId is missing");
        }

        // Every blocked seat shares one request identifier, so the seat-change stream groups them as one change.
        var change = new SeatChange(Guid.NewGuid().ToString("D"), receivedEvent.CorrelationId, _clock.UtcNow.UtcDateTime.ToString("O"));
        for (int attempt = 1; attempt <= MaxWriteAttempts; attempt++)
        {
            Cancellation? cancellation = await TryCancelAsync(message.TripId.Value, change, cancellationToken);
            if (cancellation != null)
            {
                LogApplied(receivedEvent, message, cancellation);
                return EventOutcome.Applied();
            }
        }

        // Nothing was changed, and a cancellation is safe to repeat, so the queue's next delivery settles it.
        throw new InvalidOperationException(
            $"Cancelling trip '{message.TripId}' lost {MaxWriteAttempts} races in a row; nothing changed.");
    }

    // Null means the write lost a race since the read, and the cancellation should be decided again.
    private async Task<Cancellation?> TryCancelAsync(Guid tripId, SeatChange change, CancellationToken cancellationToken)
    {
        List<Dictionary<string, AttributeValue>> items = await ListTripItemsAsync(tripId, cancellationToken);
        Dictionary<string, AttributeValue>? trip = items.Find(IsTripItem);
        if (trip != null && IsCancelled(trip))
        {
            return new Cancellation(false, 0);
        }

        List<TransactWriteItem> writes = PlanWrites(tripId, items, trip != null, change);
        try
        {
            await _dynamoDb.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = writes }, cancellationToken);
        }
        catch (TransactionCanceledException exception) when (IsLostRace(exception))
        {
            return null;
        }

        int blockedCount = items.Count(item => !IsTripItem(item) && IsFree(item));
        return new Cancellation(true, blockedCount);
    }

    // The trip's mark, a block for every free seat, and for every other seat a check that it is still as
    // read: a seat released meanwhile would otherwise stay Available on a cancelled trip.
    private List<TransactWriteItem> PlanWrites(
        Guid tripId, List<Dictionary<string, AttributeValue>> items, bool hasSeats, SeatChange change)
    {
        var writes = new List<TransactWriteItem> { MarkCancelled(tripId, hasSeats, change) };
        foreach (Dictionary<string, AttributeValue> seat in items)
        {
            if (IsTripItem(seat))
            {
                continue;
            }

            if (IsFree(seat))
            {
                writes.Add(BlockSeat(tripId, seat, change));
            }
            else
            {
                writes.Add(SeatUnchanged(tripId, seat));
            }
        }

        return writes;
    }

    private TransactWriteItem MarkCancelled(Guid tripId, bool hasSeats, SeatChange change)
    {
        string condition = StillOpenWithoutSeats;
        if (hasSeats)
        {
            condition = StillOpen;
        }

        var update = new Update
        {
            TableName = _tableName,
            Key = Key(tripId, TripSortKey),
            UpdateExpression = "SET isCancelled = :true, cancelledAt = :now",
            ConditionExpression = condition,
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":true", new AttributeValue { BOOL = true } },
                { ":false", new AttributeValue { BOOL = false } },
                { ":now", new AttributeValue { S = change.ChangedAt } },
            },
        };
        return new TransactWriteItem { Update = update };
    }

    private TransactWriteItem BlockSeat(Guid tripId, Dictionary<string, AttributeValue> seat, SeatChange change)
    {
        var update = new Update
        {
            TableName = _tableName,
            Key = Key(tripId, SeatSortKeyPrefix + seat["seatNo"].S),
            UpdateExpression = BlockForCancellation,
            ConditionExpression = "#status = :was AND (attribute_not_exists(blockReason) OR blockReason = :operator)",
            ExpressionAttributeNames = new Dictionary<string, string> { { "#status", "status" } },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":was", new AttributeValue { S = seat["status"].S } },
                { ":operator", new AttributeValue { S = nameof(BlockReason.Operator) } },
                { ":blocked", new AttributeValue { S = nameof(SeatStatus.Blocked) } },
                { ":tripCancelled", new AttributeValue { S = nameof(BlockReason.TripCancelled) } },
                { ":reason", new AttributeValue { S = ChangeReason } },
                { ":request", new AttributeValue { S = change.Request } },
                { ":correlation", new AttributeValue { S = change.CorrelationId } },
                { ":now", new AttributeValue { S = change.ChangedAt } },
            },
        };
        return new TransactWriteItem { Update = update };
    }

    private TransactWriteItem SeatUnchanged(Guid tripId, Dictionary<string, AttributeValue> seat)
    {
        var check = new ConditionCheck
        {
            TableName = _tableName,
            Key = Key(tripId, SeatSortKeyPrefix + seat["seatNo"].S),
            ConditionExpression = "#status = :was",
            ExpressionAttributeNames = new Dictionary<string, string> { { "#status", "status" } },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":was", new AttributeValue { S = seat["status"].S } },
            },
        };
        return new TransactWriteItem { ConditionCheck = check };
    }

    // Free means sellable now or blocked only by the operator; both are blocked for the cancellation.
    private static bool IsFree(Dictionary<string, AttributeValue> seat)
    {
        string status = seat["status"].S;
        if (status == nameof(SeatStatus.Available))
        {
            return true;
        }

        bool isOperatorBlock = seat.ContainsKey("blockReason") && seat["blockReason"].S == nameof(BlockReason.Operator);
        return status == nameof(SeatStatus.Blocked) && isOperatorBlock;
    }

    private void LogApplied(ReceivedEvent receivedEvent, TripCancelledMessage message, Cancellation cancellation)
    {
        if (!cancellation.IsCancelled)
        {
            _logger.LogInformation(
                "TripCancelled event {EventId} (correlation {CorrelationId}) changed nothing: trip {TripId} was already cancelled",
                receivedEvent.EventId, receivedEvent.CorrelationId, message.TripId);
            return;
        }

        // The reason is kept on this log line only: no seat or event carries it.
        _logger.LogInformation(
            "TripCancelled event {EventId} (correlation {CorrelationId}) cancelled trip {TripId} and blocked {SeatCount} seats, reason {CancellationReason}",
            receivedEvent.EventId, receivedEvent.CorrelationId, message.TripId, cancellation.BlockedSeatCount, message.Reason);
    }

    // Data that does not even have TripCancelled's shape gets null, and is reported rather than guessed at.
    private static TripCancelledMessage? ReadMessage(JsonElement data)
    {
        try
        {
            return data.Deserialize<TripCancelledMessage>(WireJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<List<Dictionary<string, AttributeValue>>> ListTripItemsAsync(Guid tripId, CancellationToken cancellationToken)
    {
        // Strongly consistent, so a decision made after a lost race sees the winner's change.
        var query = new QueryRequest
        {
            TableName = _tableName,
            KeyConditionExpression = "pk = :pk",
            ProjectionExpression = Projection,
            ConsistentRead = true,
            ExpressionAttributeNames = new Dictionary<string, string> { { "#status", "status" } },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":pk", new AttributeValue { S = TripKeyPrefix + tripId.ToString("D") } },
            },
        };

        var items = new List<Dictionary<string, AttributeValue>>();
        do
        {
            QueryResponse page = await _dynamoDb.QueryAsync(query, cancellationToken);
            if (page.Items != null)
            {
                items.AddRange(page.Items);
            }

            query.ExclusiveStartKey = page.LastEvaluatedKey;
        }
        while (query.ExclusiveStartKey != null && query.ExclusiveStartKey.Count > 0);

        return items;
    }

    private static bool IsCancelled(Dictionary<string, AttributeValue> trip)
    {
        return trip.ContainsKey("isCancelled") && trip["isCancelled"].BOOL == true;
    }

    private static bool IsTripItem(Dictionary<string, AttributeValue> item)
    {
        return item["sk"].S == TripSortKey;
    }

    private static bool IsLostRace(TransactionCanceledException exception)
    {
        if (exception.CancellationReasons is null)
        {
            return false;
        }

        return exception.CancellationReasons.Exists(reason => reason.Code == ConditionFailed || reason.Code == TransactionConflict);
    }

    private static Dictionary<string, AttributeValue> Key(Guid tripId, string sortKey)
    {
        return new Dictionary<string, AttributeValue>
        {
            { "pk", new AttributeValue { S = TripKeyPrefix + tripId.ToString("D") } },
            { "sk", new AttributeValue { S = sortKey } },
        };
    }

    /// <summary>What every change of one cancellation shares, whichever attempt writes it.</summary>
    /// <param name="Request">The request identifier the seat-change stream groups by.</param>
    /// <param name="CorrelationId">The correlation identifier of the Fleet request that cancelled the trip.</param>
    /// <param name="ChangedAt">When Seat Inventory learned of the cancellation, ISO-8601 UTC.</param>
    private sealed record SeatChange(string Request, string CorrelationId, string ChangedAt);

    /// <summary>What one attempt did.</summary>
    /// <param name="IsCancelled">False when the trip was already cancelled, so nothing changed.</param>
    /// <param name="BlockedSeatCount">The seats blocked for the cancellation.</param>
    private sealed record Cancellation(bool IsCancelled, int BlockedSeatCount);
}
