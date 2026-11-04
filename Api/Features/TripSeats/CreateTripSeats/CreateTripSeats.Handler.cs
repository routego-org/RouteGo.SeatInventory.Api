namespace Api.Features.TripSeats.CreateTripSeats;

/// <summary>
/// Creates Seat Inventory's record of a trip Fleet announced — its operator and departure time — and one
/// seat per seat in the announcement, all in one DynamoDB transaction, so a trip has every seat or none.
/// Seats start Available, or Blocked with block reason TripCancelled when the trip's cancellation arrived
/// first, because a cancelled trip's seats never go on sale. Repeat-safe by the trip identifier: the trip
/// record keeps when its seats were created, and the write is conditional on that mark being absent, so
/// a second delivery — or any later TripScheduled for the same trip — changes nothing. Each seat is a new
/// item, which the seat-change stream does not announce.
/// </summary>
public sealed class CreateTripSeatsHandler
{
    /// <summary>The Fleet event type that starts this worker.</summary>
    public const string TriggerEventType = "TripScheduled";

    private const string TripKeyPrefix = "TRIP#";
    private const string TripSortKey = "TRIP";
    private const string SeatSortKeyPrefix = "SEAT#";
    private const string ChangeReason = "Created";
    private const string ConditionFailed = "ConditionalCheckFailed";
    private const string TransactionConflict = "TransactionConflict";

    // Creation can lose a race with the trip's cancellation, or with another API copy handling the same
    // event; a few attempts, each after a fresh read, settle it on what the trip is now.
    private const int MaxWriteAttempts = 3;

    private const string CreateTrip =
        "SET operatorId = :operator, departureAt = :departure, seatsCreatedAt = :now, "
        + "isCancelled = if_not_exists(isCancelled, :false)";

    private const string NotCreatedAndStillOpen =
        "attribute_not_exists(seatsCreatedAt) AND (attribute_not_exists(isCancelled) OR isCancelled = :false)";

    private const string NotCreated = "attribute_not_exists(seatsCreatedAt)";

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;
    private readonly IClock _clock;
    private readonly ILogger<CreateTripSeatsHandler> _logger;

    public CreateTripSeatsHandler(IAmazonDynamoDB dynamoDb, IConfiguration configuration, IClock clock, ILogger<CreateTripSeatsHandler> logger)
    {
        _dynamoDb = dynamoDb;
        // Present by the time any event arrives: startup refuses to run without it.
        _tableName = configuration[Infrastructure.ConfigKeys.SeatStoreTableName]!;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Applies one TripScheduled event and says what should happen to its queue message.</summary>
    public async Task<EventOutcome> HandleAsync(ReceivedEvent receivedEvent, CancellationToken cancellationToken)
    {
        TripScheduledMessage? message = ReadMessage(receivedEvent.Data);
        string? problem = DescribeProblem(message);
        if (problem != null)
        {
            return EventOutcome.Rejected(problem);
        }

        for (int attempt = 1; attempt <= MaxWriteAttempts; attempt++)
        {
            Creation? creation = await TryCreateAsync(message!, receivedEvent.CorrelationId, cancellationToken);
            if (creation != null)
            {
                LogApplied(receivedEvent, message!, creation);
                return EventOutcome.Applied();
            }
        }

        // Nothing was created, and creating is safe to repeat, so the queue's next delivery settles it.
        throw new InvalidOperationException(
            $"Creating the seats of trip '{message!.TripId}' lost {MaxWriteAttempts} races in a row; nothing changed.");
    }

    private static string? DescribeProblem(TripScheduledMessage? message)
    {
        if (message is null)
        {
            return "data is not a TripScheduled trip";
        }

        return message.DescribeProblem();
    }

    // Null means the write lost a race since the read, and creation should be decided again.
    private async Task<Creation?> TryCreateAsync(TripScheduledMessage message, string correlationId, CancellationToken cancellationToken)
    {
        Dictionary<string, AttributeValue>? trip = await GetTripAsync(message.TripId!.Value, cancellationToken);
        if (trip != null && trip.ContainsKey("seatsCreatedAt"))
        {
            return new Creation(false, IsCancelled(trip));
        }

        bool isTripCancelled = trip != null && IsCancelled(trip);
        var change = new SeatChange(Guid.NewGuid().ToString("D"), correlationId, _clock.UtcNow.UtcDateTime.ToString("O"));
        var writes = new List<TransactWriteItem> { TripWrite(message, isTripCancelled, change) };
        foreach (TripScheduledSeat? seat in message.Seats!)
        {
            writes.Add(SeatWrite(message.TripId.Value, seat!, isTripCancelled, change));
        }

        try
        {
            await _dynamoDb.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = writes }, cancellationToken);
        }
        catch (TransactionCanceledException exception) when (IsLostRace(exception))
        {
            return null;
        }

        return new Creation(true, isTripCancelled);
    }

    // The trip's operator and departure are set here once, together with the mark that its seats exist.
    // Seats created Available need the trip still open: a cancellation landing meanwhile fails this
    // check, and the next attempt creates them Blocked instead.
    private TransactWriteItem TripWrite(TripScheduledMessage message, bool isTripCancelled, SeatChange change)
    {
        string condition = NotCreatedAndStillOpen;
        if (isTripCancelled)
        {
            condition = NotCreated;
        }

        var update = new Update
        {
            TableName = _tableName,
            Key = Key(message.TripId!.Value, TripSortKey),
            UpdateExpression = CreateTrip,
            ConditionExpression = condition,
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":operator", new AttributeValue { S = Guid.Parse(message.OperatorId!).ToString("D") } },
                { ":departure", new AttributeValue { S = message.DepartureUtc!.Value.UtcDateTime.ToString("O") } },
                { ":now", new AttributeValue { S = change.ChangedAt } },
                { ":false", new AttributeValue { BOOL = false } },
            },
        };
        return new TransactWriteItem { Update = update };
    }

    private TransactWriteItem SeatWrite(Guid tripId, TripScheduledSeat seat, bool isTripCancelled, SeatChange change)
    {
        Dictionary<string, AttributeValue> item = Key(tripId, SeatSortKeyPrefix + seat.SeatNo);
        item["seatNo"] = new AttributeValue { S = seat.SeatNo };
        item["deck"] = new AttributeValue { S = seat.Deck };
        item["row"] = Number(seat.Row!.Value);
        item["column"] = Number(seat.Col!.Value);
        item["seatClass"] = new AttributeValue { S = seat.Class };
        item["fare"] = Number(seat.Fare!.Value);
        item["status"] = new AttributeValue { S = nameof(SeatStatus.Available) };
        if (isTripCancelled)
        {
            item["status"] = new AttributeValue { S = nameof(SeatStatus.Blocked) };
            item["blockReason"] = new AttributeValue { S = nameof(BlockReason.TripCancelled) };
        }

        item["lastChangeReason"] = new AttributeValue { S = ChangeReason };
        item["lastChangeRequest"] = new AttributeValue { S = change.Request };
        item["lastChangeCorrelationId"] = new AttributeValue { S = change.CorrelationId };
        item["lastChangedAt"] = new AttributeValue { S = change.ChangedAt };
        var put = new Put { TableName = _tableName, Item = item, ConditionExpression = "attribute_not_exists(pk)" };
        return new TransactWriteItem { Put = put };
    }

    private void LogApplied(ReceivedEvent receivedEvent, TripScheduledMessage message, Creation creation)
    {
        if (!creation.IsCreated)
        {
            _logger.LogInformation(
                "TripScheduled event {EventId} (correlation {CorrelationId}) changed nothing: trip {TripId} already has its seats",
                receivedEvent.EventId, receivedEvent.CorrelationId, message.TripId);
            return;
        }

        _logger.LogInformation(
            "TripScheduled event {EventId} (correlation {CorrelationId}) created trip {TripId} with {SeatCount} seats, trip cancelled {IsCancelled}",
            receivedEvent.EventId, receivedEvent.CorrelationId, message.TripId, message.Seats!.Count, creation.IsCancelled);
    }

    // Data that does not even have TripScheduled's shape gets null, and is reported rather than guessed at.
    private static TripScheduledMessage? ReadMessage(JsonElement data)
    {
        try
        {
            return data.Deserialize<TripScheduledMessage>(WireJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<Dictionary<string, AttributeValue>?> GetTripAsync(Guid tripId, CancellationToken cancellationToken)
    {
        // Strongly consistent, so a decision made after a lost race sees the winner's change.
        var request = new GetItemRequest
        {
            TableName = _tableName,
            Key = Key(tripId, TripSortKey),
            ProjectionExpression = "isCancelled, seatsCreatedAt",
            ConsistentRead = true,
        };
        GetItemResponse response = await _dynamoDb.GetItemAsync(request, cancellationToken);
        if (response.Item is null || response.Item.Count == 0)
        {
            return null;
        }

        return response.Item;
    }

    private static bool IsCancelled(Dictionary<string, AttributeValue> trip)
    {
        return trip.ContainsKey("isCancelled") && trip["isCancelled"].BOOL == true;
    }

    private static bool IsLostRace(TransactionCanceledException exception)
    {
        if (exception.CancellationReasons is null)
        {
            return false;
        }

        return exception.CancellationReasons.Exists(reason => reason.Code == ConditionFailed || reason.Code == TransactionConflict);
    }

    private static AttributeValue Number(int value)
    {
        return new AttributeValue { N = value.ToString(System.Globalization.CultureInfo.InvariantCulture) };
    }

    private static Dictionary<string, AttributeValue> Key(Guid tripId, string sortKey)
    {
        return new Dictionary<string, AttributeValue>
        {
            { "pk", new AttributeValue { S = TripKeyPrefix + tripId.ToString("D") } },
            { "sk", new AttributeValue { S = sortKey } },
        };
    }

    /// <summary>The change record every seat of one creation shares.</summary>
    /// <param name="Request">The request identifier the seat-change stream groups by.</param>
    /// <param name="CorrelationId">The correlation identifier of the Fleet request that scheduled the trip.</param>
    /// <param name="ChangedAt">When the seats were created, ISO-8601 UTC.</param>
    private sealed record SeatChange(string Request, string CorrelationId, string ChangedAt);

    /// <summary>What one attempt did.</summary>
    /// <param name="IsCreated">False when the trip already had its seats, so nothing changed.</param>
    /// <param name="IsCancelled">Whether the trip is cancelled, so its seats were created Blocked.</param>
    private sealed record Creation(bool IsCreated, bool IsCancelled);
}
