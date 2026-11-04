namespace Api.Features.TripSeats.UpdateTripFares;

/// <summary>
/// Replaces the fares of a re-priced trip's seats, whatever each seat's status, in one DynamoDB
/// transaction, so the trip's fares change together or not at all. The latest change wins: the trip
/// records when the last applied change occurred, and the write is conditional on this change having
/// occurred after it — so the same event again, or an older one arriving late, changes nothing. A change
/// for a trip whose seats do not exist yet is postponed until they do. Every re-priced seat records
/// change reason FareChanged, which the seat-change stream does not announce.
/// </summary>
public sealed class UpdateTripFaresHandler
{
    /// <summary>The Fleet event type that starts this worker.</summary>
    public const string TriggerEventType = "FareChanged";

    // The alert reason for a fare change whose trip is still not known after its last delivery.
    private const string TripUnknown = "tripUnknown";

    private const string TripKeyPrefix = "TRIP#";
    private const string TripSortKey = "TRIP";
    private const string SeatSortKeyPrefix = "SEAT#";
    private const string ChangeReason = "FareChanged";
    private const string ConditionFailed = "ConditionalCheckFailed";
    private const string TransactionConflict = "TransactionConflict";

    // A fare change can lose a race with another fare change of the same trip; a few attempts, each after
    // a fresh read, settle which one is the latest.
    private const int MaxWriteAttempts = 3;

    private const string Projection = "sk, seatsCreatedAt, faresChangedAt, seatNo";

    private const string ReplaceFare =
        "SET fare = :fare, lastChangeReason = :reason, lastChangeRequest = :request, "
        + "lastChangeCorrelationId = :correlation, lastChangedAt = :now "
        + "REMOVE lastChangedBy, lastChangeNote, lastChangeMovedFrom";

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;
    private readonly IClock _clock;
    private readonly ILogger<UpdateTripFaresHandler> _logger;

    public UpdateTripFaresHandler(IAmazonDynamoDB dynamoDb, IConfiguration configuration, IClock clock, ILogger<UpdateTripFaresHandler> logger)
    {
        _dynamoDb = dynamoDb;
        // Present by the time any event arrives: startup refuses to run without it.
        _tableName = configuration[Infrastructure.ConfigKeys.SeatStoreTableName]!;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Applies one FareChanged event and says what should happen to its queue message.</summary>
    public async Task<EventOutcome> HandleAsync(ReceivedEvent receivedEvent, CancellationToken cancellationToken)
    {
        FareChangedMessage? message = ReadMessage(receivedEvent.Data);
        string? problem = DescribeProblem(message);
        if (problem != null)
        {
            return EventOutcome.Rejected(problem);
        }

        // Every re-priced seat shares one request identifier, so the seat-change stream groups them as one change.
        var change = new FareChange
        {
            Request = Guid.NewGuid().ToString("D"),
            CorrelationId = receivedEvent.CorrelationId,
            OccurredAt = receivedEvent.OccurredAt.UtcDateTime.ToString("O"),
            ChangedAt = _clock.UtcNow.UtcDateTime.ToString("O"),
        };
        for (int attempt = 1; attempt <= MaxWriteAttempts; attempt++)
        {
            EventOutcome? outcome = await TryUpdateAsync(receivedEvent, message!, change, cancellationToken);
            if (outcome != null)
            {
                return outcome;
            }
        }

        // Nothing was changed, and a fare change is safe to repeat, so the queue's next delivery settles it.
        throw new InvalidOperationException(
            $"Updating the fares of trip '{message!.TripId}' lost {MaxWriteAttempts} races in a row; nothing changed.");
    }

    private static string? DescribeProblem(FareChangedMessage? message)
    {
        if (message is null)
        {
            return "data is not a FareChanged trip";
        }

        return message.DescribeProblem();
    }

    // Null means the write lost a race since the read, and the change should be decided again.
    private async Task<EventOutcome?> TryUpdateAsync(
        ReceivedEvent receivedEvent, FareChangedMessage message, FareChange change, CancellationToken cancellationToken)
    {
        List<Dictionary<string, AttributeValue>> items = await ListTripItemsAsync(message.TripId!.Value, cancellationToken);
        Dictionary<string, AttributeValue>? trip = items.Find(IsTripItem);
        if (trip is null || !trip.ContainsKey("seatsCreatedAt"))
        {
            return EventOutcome.Postponed(TripUnknown, $"trip {message.TripId} has no seats yet");
        }

        string? unknownSeats = DescribeUnknownSeats(items, message);
        if (unknownSeats != null)
        {
            return EventOutcome.Rejected(unknownSeats);
        }

        bool isLatest = IsLaterThanLastApplied(trip, change.OccurredAt);
        if (isLatest)
        {
            bool isWritten = await TryWriteAsync(message, change, cancellationToken);
            if (!isWritten)
            {
                return null;
            }
        }

        LogApplied(receivedEvent, message, isLatest);
        return EventOutcome.Applied();
    }

    private static string? DescribeUnknownSeats(List<Dictionary<string, AttributeValue>> items, FareChangedMessage message)
    {
        var tripSeatNos = new HashSet<string>(StringComparer.Ordinal);
        foreach (Dictionary<string, AttributeValue> item in items)
        {
            if (!IsTripItem(item))
            {
                tripSeatNos.Add(item["seatNo"].S);
            }
        }

        var unknown = new List<string>();
        foreach (FareChangedSeat? seat in message.Seats!)
        {
            if (!tripSeatNos.Contains(seat!.SeatNo!))
            {
                unknown.Add(seat.SeatNo!);
            }
        }

        if (unknown.Count == 0)
        {
            return null;
        }

        return $"trip {message.TripId} has no seats {string.Join(", ", unknown)}";
    }

    // Both times are ISO-8601 UTC with seven fraction digits, so text order is time order.
    private static bool IsLaterThanLastApplied(Dictionary<string, AttributeValue> trip, string occurredAt)
    {
        if (!trip.ContainsKey("faresChangedAt"))
        {
            return true;
        }

        return string.CompareOrdinal(occurredAt, trip["faresChangedAt"].S) > 0;
    }

    private async Task<bool> TryWriteAsync(FareChangedMessage message, FareChange change, CancellationToken cancellationToken)
    {
        Guid tripId = message.TripId!.Value;
        var writes = new List<TransactWriteItem> { MarkLatest(tripId, change) };
        foreach (FareChangedSeat? seat in message.Seats!)
        {
            writes.Add(ReplaceSeatFare(tripId, seat!, change));
        }

        try
        {
            await _dynamoDb.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = writes }, cancellationToken);
        }
        catch (TransactionCanceledException exception) when (IsLostRace(exception))
        {
            return false;
        }

        return true;
    }

    // A later change landing meanwhile fails this check, and the next attempt finds this one stale.
    private TransactWriteItem MarkLatest(Guid tripId, FareChange change)
    {
        var update = new Update
        {
            TableName = _tableName,
            Key = Key(tripId, TripSortKey),
            UpdateExpression = "SET faresChangedAt = :occurred",
            ConditionExpression = "attribute_exists(seatsCreatedAt) AND (attribute_not_exists(faresChangedAt) OR faresChangedAt < :occurred)",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":occurred", new AttributeValue { S = change.OccurredAt } },
            },
        };
        return new TransactWriteItem { Update = update };
    }

    private TransactWriteItem ReplaceSeatFare(Guid tripId, FareChangedSeat seat, FareChange change)
    {
        var update = new Update
        {
            TableName = _tableName,
            Key = Key(tripId, SeatSortKeyPrefix + seat.SeatNo),
            UpdateExpression = ReplaceFare,
            ConditionExpression = "attribute_exists(pk)",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":fare", new AttributeValue { N = seat.Fare!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) } },
                { ":reason", new AttributeValue { S = ChangeReason } },
                { ":request", new AttributeValue { S = change.Request } },
                { ":correlation", new AttributeValue { S = change.CorrelationId } },
                { ":now", new AttributeValue { S = change.ChangedAt } },
            },
        };
        return new TransactWriteItem { Update = update };
    }

    private void LogApplied(ReceivedEvent receivedEvent, FareChangedMessage message, bool isUpdated)
    {
        if (!isUpdated)
        {
            _logger.LogInformation(
                "FareChanged event {EventId} (correlation {CorrelationId}) changed nothing: trip {TripId} already has a fare change that occurred at or after {OccurredAt}",
                receivedEvent.EventId, receivedEvent.CorrelationId, message.TripId, receivedEvent.OccurredAt);
            return;
        }

        _logger.LogInformation(
            "FareChanged event {EventId} (correlation {CorrelationId}) replaced the fares of {SeatCount} seats of trip {TripId}",
            receivedEvent.EventId, receivedEvent.CorrelationId, message.Seats!.Count, message.TripId);
    }

    // Data that does not even have FareChanged's shape gets null, and is reported rather than guessed at.
    private static FareChangedMessage? ReadMessage(JsonElement data)
    {
        try
        {
            return data.Deserialize<FareChangedMessage>(WireJson.Options);
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

    /// <summary>What every write of one fare change shares, whichever attempt writes it.</summary>
    private sealed record FareChange
    {
        /// <summary>The request identifier the seat-change stream groups by.</summary>
        public required string Request { get; init; }

        /// <summary>The correlation identifier of the Fleet request that re-priced the trip.</summary>
        public required string CorrelationId { get; init; }

        /// <summary>When Fleet re-priced the trip, ISO-8601 UTC; what orders fare changes.</summary>
        public required string OccurredAt { get; init; }

        /// <summary>When Seat Inventory applied it, ISO-8601 UTC.</summary>
        public required string ChangedAt { get; init; }
    }
}
