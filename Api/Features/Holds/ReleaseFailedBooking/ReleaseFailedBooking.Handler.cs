namespace Api.Features.Holds.ReleaseFailedBooking;

/// <summary>
/// Frees the seats a failed booking still holds on its trip — Held ones only; a Booked seat is freed only
/// by Booking's cancellation. They go back to Available, or to Blocked when the trip is cancelled, because
/// a cancelled trip's seats never go back on sale. Repeat-safe by its condition: each seat must still be
/// Held by this booking, so a second delivery — or a BookingFailed whose hold already expired or was
/// released — finds no such seat and changes nothing. The change is one conditional DynamoDB transaction,
/// and every freed seat records the change the seat-change stream turns into SeatReleased with reason
/// BookingFailed.
/// </summary>
public sealed class ReleaseFailedBookingHandler
{
    /// <summary>The Booking event type that starts this worker.</summary>
    public const string TriggerEventType = "BookingFailed";

    private const string TripKeyPrefix = "TRIP#";
    private const string TripSortKey = "TRIP";
    private const string SeatSortKeyPrefix = "SEAT#";
    private const string ChangeReason = "BookingFailed";
    private const string ConditionFailed = "ConditionalCheckFailed";
    private const string TransactionConflict = "TransactionConflict";

    // A release can lose a race with a confirm, an expiry or the trip's cancellation; a few attempts, each
    // after a fresh read, settle it on what the seats and the trip are now.
    private const int MaxWriteAttempts = 3;

    // STATUS is a DynamoDB reserved word, hence the placeholder.
    private const string Projection = "sk, isCancelled, seatNo, #status, bookingId";

    private const string ReleaseToAvailable =
        "SET #status = :available, lastChangeReason = :reason, lastChangeRequest = :request, "
        + "lastChangeCorrelationId = :correlation, lastChangedAt = :now "
        + "REMOVE bookingId, holdExpiresAt, blockReason, lastChangedBy, lastChangeNote, lastChangeMovedFrom";

    private const string ReleaseToBlocked =
        "SET #status = :blocked, blockReason = :tripCancelled, lastChangeReason = :reason, lastChangeRequest = :request, "
        + "lastChangeCorrelationId = :correlation, lastChangedAt = :now "
        + "REMOVE bookingId, holdExpiresAt, lastChangedBy, lastChangeNote, lastChangeMovedFrom";

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;
    private readonly IClock _clock;
    private readonly ILogger<ReleaseFailedBookingHandler> _logger;

    public ReleaseFailedBookingHandler(
        IAmazonDynamoDB dynamoDb, IConfiguration configuration, IClock clock, ILogger<ReleaseFailedBookingHandler> logger)
    {
        _dynamoDb = dynamoDb;
        // Present by the time any event arrives: startup refuses to run without it.
        _tableName = configuration[Infrastructure.ConfigKeys.SeatStoreTableName]!;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Applies one BookingFailed event and says what should happen to its queue message.</summary>
    public async Task<EventOutcome> HandleAsync(ReceivedEvent receivedEvent, CancellationToken cancellationToken)
    {
        BookingFailedMessage? message = ReadMessage(receivedEvent.Data);
        if (message is null || message.BookingId is null || message.TripId is null)
        {
            return EventOutcome.Rejected("bookingId or tripId is missing");
        }

        // Every freed seat shares one request identifier, so the seat-change stream groups them back
        // into one SeatReleased event.
        var change = new SeatChange(Guid.NewGuid().ToString("D"), receivedEvent.CorrelationId, _clock.UtcNow.UtcDateTime.ToString("O"));
        for (int attempt = 1; attempt <= MaxWriteAttempts; attempt++)
        {
            ReleaseFailedBookingResult? result = await TryReleaseAsync(message, change, cancellationToken);
            if (result != null)
            {
                _logger.LogInformation(
                    "BookingFailed event {EventId} (correlation {CorrelationId}) freed {SeatCount} seats of booking {BookingId} on trip {TripId}",
                    receivedEvent.EventId, receivedEvent.CorrelationId, result.ReleasedSeatNos.Count, message.BookingId, message.TripId);
                return EventOutcome.Applied();
            }
        }

        // Nothing was changed, and a release is safe to repeat, so the queue's next delivery settles it.
        throw new InvalidOperationException(
            $"Releasing booking '{message.BookingId}' on trip '{message.TripId}' lost {MaxWriteAttempts} races in a row; nothing changed.");
    }

    // Null means the write lost a race since the read, and the release should be decided again.
    private async Task<ReleaseFailedBookingResult?> TryReleaseAsync(
        BookingFailedMessage message, SeatChange change, CancellationToken cancellationToken)
    {
        List<Dictionary<string, AttributeValue>> items = await ListTripItemsAsync(message.TripId!.Value, cancellationToken);
        Dictionary<string, AttributeValue>? trip = items.Find(IsTripItem);
        List<string> stillHeld = SeatsStillHeld(items, message.BookingId!.Value);
        if (trip is null || stillHeld.Count == 0)
        {
            return new ReleaseFailedBookingResult(Array.Empty<string>());
        }

        bool isTripCancelled = IsCancelled(trip);
        var writes = new List<TransactWriteItem>();
        // Going back on sale is safe only while the trip is still open; a cancellation landing meanwhile
        // fails this check, and the next attempt blocks the seats instead.
        if (!isTripCancelled)
        {
            writes.Add(TripStillOpen(message.TripId.Value));
        }

        foreach (string seatNo in stillHeld)
        {
            writes.Add(ReleaseSeat(message, seatNo, isTripCancelled, change));
        }

        bool isWritten = await TryWriteAsync(writes, cancellationToken);
        if (!isWritten)
        {
            return null;
        }

        return new ReleaseFailedBookingResult(stillHeld);
    }

    private static List<string> SeatsStillHeld(List<Dictionary<string, AttributeValue>> items, Guid bookingId)
    {
        string booking = bookingId.ToString("D");
        var seatNos = new List<string>();
        foreach (Dictionary<string, AttributeValue> item in items)
        {
            bool isThisBooking = !IsTripItem(item) && item.ContainsKey("bookingId") && item["bookingId"].S == booking;
            if (isThisBooking && item["status"].S == nameof(SeatStatus.Held))
            {
                seatNos.Add(item["seatNo"].S);
            }
        }

        return seatNos;
    }

    private async Task<bool> TryWriteAsync(List<TransactWriteItem> writes, CancellationToken cancellationToken)
    {
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

    private TransactWriteItem TripStillOpen(Guid tripId)
    {
        return new TransactWriteItem
        {
            ConditionCheck = new ConditionCheck
            {
                TableName = _tableName,
                Key = Key(tripId, TripSortKey),
                ConditionExpression = "attribute_not_exists(isCancelled) OR isCancelled = :false",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    { ":false", new AttributeValue { BOOL = false } },
                },
            },
        };
    }

    private TransactWriteItem ReleaseSeat(BookingFailedMessage message, string seatNo, bool isTripCancelled, SeatChange change)
    {
        Dictionary<string, AttributeValue> values = ReleaseValues(message.BookingId!.Value, change);
        string updateExpression = ReleaseToAvailable;
        if (isTripCancelled)
        {
            updateExpression = ReleaseToBlocked;
            values[":blocked"] = new AttributeValue { S = nameof(SeatStatus.Blocked) };
            values[":tripCancelled"] = new AttributeValue { S = nameof(BlockReason.TripCancelled) };
        }
        else
        {
            values[":available"] = new AttributeValue { S = nameof(SeatStatus.Available) };
        }

        var update = new Update
        {
            TableName = _tableName,
            Key = Key(message.TripId!.Value, SeatSortKeyPrefix + seatNo),
            UpdateExpression = updateExpression,
            ConditionExpression = "#status = :held AND bookingId = :booking",
            ExpressionAttributeNames = new Dictionary<string, string> { { "#status", "status" } },
            ExpressionAttributeValues = values,
        };
        return new TransactWriteItem { Update = update };
    }

    // The values every freed seat's update needs, whichever status it goes to.
    private static Dictionary<string, AttributeValue> ReleaseValues(Guid bookingId, SeatChange change)
    {
        return new Dictionary<string, AttributeValue>
        {
            { ":held", new AttributeValue { S = nameof(SeatStatus.Held) } },
            { ":booking", new AttributeValue { S = bookingId.ToString("D") } },
            { ":reason", new AttributeValue { S = ChangeReason } },
            { ":request", new AttributeValue { S = change.Request } },
            { ":correlation", new AttributeValue { S = change.CorrelationId } },
            { ":now", new AttributeValue { S = change.ChangedAt } },
        };
    }

    // Data that does not even have BookingFailed's shape gets null, and is reported rather than guessed at.
    private static BookingFailedMessage? ReadMessage(JsonElement data)
    {
        try
        {
            return data.Deserialize<BookingFailedMessage>(WireJson.Options);
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

    /// <summary>The change record every seat of one release shares.</summary>
    /// <param name="Request">The request identifier the seat-change stream groups by.</param>
    /// <param name="CorrelationId">The correlation identifier of the Booking request that failed.</param>
    /// <param name="ChangedAt">When the seats were freed, ISO-8601 UTC.</param>
    private sealed record SeatChange(string Request, string CorrelationId, string ChangedAt);
}
