namespace Api.Features.Holds.ReleaseCancelledBooking;

/// <summary>
/// Frees every seat a cancelled booking holds or owns on its trip — Held or Booked; this is the only way
/// a Booked seat is ever freed. They go back to Available, or to Blocked when the trip is cancelled,
/// because a cancelled trip's seats never go back on sale. Repeat-safe by its condition: each seat must
/// still be in the status read and still belong to this booking, so a second delivery — or one arriving
/// after the seat was taken by another booking — finds no such seat and changes nothing. The change is one
/// conditional DynamoDB transaction, and every freed seat records the change the seat-change stream turns
/// into SeatReleased with reason BookingCancelled.
/// </summary>
public sealed class ReleaseCancelledBookingHandler
{
    /// <summary>The Booking event type that starts this worker.</summary>
    public const string TriggerEventType = "BookingCancelled";

    private const string TripKeyPrefix = "TRIP#";
    private const string TripSortKey = "TRIP";
    private const string SeatSortKeyPrefix = "SEAT#";
    private const string ChangeReason = "BookingCancelled";
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
    private readonly ILogger<ReleaseCancelledBookingHandler> _logger;

    public ReleaseCancelledBookingHandler(
        IAmazonDynamoDB dynamoDb, IConfiguration configuration, IClock clock, ILogger<ReleaseCancelledBookingHandler> logger)
    {
        _dynamoDb = dynamoDb;
        // Present by the time any event arrives: startup refuses to run without it.
        _tableName = configuration[Infrastructure.ConfigKeys.SeatStoreTableName]!;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Applies one BookingCancelled event and says what should happen to its queue message.</summary>
    public async Task<EventOutcome> HandleAsync(ReceivedEvent receivedEvent, CancellationToken cancellationToken)
    {
        BookingCancelledMessage? message = ReadMessage(receivedEvent.Data);
        if (message is null || message.BookingId is null || message.TripId is null)
        {
            return EventOutcome.Rejected("bookingId or tripId is missing");
        }

        // Every freed seat shares one request identifier, so the seat-change stream groups them back
        // into one SeatReleased event.
        var change = new SeatChange(Guid.NewGuid().ToString("D"), receivedEvent.CorrelationId, _clock.UtcNow.UtcDateTime.ToString("O"));
        for (int attempt = 1; attempt <= MaxWriteAttempts; attempt++)
        {
            ReleaseCancelledBookingResult? result = await TryReleaseAsync(message, change, cancellationToken);
            if (result != null)
            {
                _logger.LogInformation(
                    "BookingCancelled event {EventId} (correlation {CorrelationId}) freed {SeatCount} seats of booking {BookingId} on trip {TripId}",
                    receivedEvent.EventId, receivedEvent.CorrelationId, result.ReleasedSeatNos.Count, message.BookingId, message.TripId);
                return EventOutcome.Applied();
            }
        }

        // Nothing was changed, and a release is safe to repeat, so the queue's next delivery settles it.
        throw new InvalidOperationException(
            $"Releasing booking '{message.BookingId}' on trip '{message.TripId}' lost {MaxWriteAttempts} races in a row; nothing changed.");
    }

    // Null means the write lost a race since the read, and the release should be decided again.
    private async Task<ReleaseCancelledBookingResult?> TryReleaseAsync(
        BookingCancelledMessage message, SeatChange change, CancellationToken cancellationToken)
    {
        List<Dictionary<string, AttributeValue>> items = await ListTripItemsAsync(message.TripId!.Value, cancellationToken);
        Dictionary<string, AttributeValue>? trip = items.Find(IsTripItem);
        List<OwnedSeat> owned = SeatsStillOwned(items, message.BookingId!.Value);
        if (trip is null || owned.Count == 0)
        {
            return new ReleaseCancelledBookingResult(Array.Empty<string>());
        }

        bool isTripCancelled = IsCancelled(trip);
        var writes = new List<TransactWriteItem>();
        // Going back on sale is safe only while the trip is still open; a cancellation landing meanwhile
        // fails this check, and the next attempt blocks the seats instead.
        if (!isTripCancelled)
        {
            writes.Add(TripStillOpen(message.TripId.Value));
        }

        foreach (OwnedSeat seat in owned)
        {
            writes.Add(ReleaseSeat(message, seat, isTripCancelled, change));
        }

        bool isWritten = await TryWriteAsync(writes, cancellationToken);
        if (!isWritten)
        {
            return null;
        }

        return new ReleaseCancelledBookingResult(owned.ConvertAll(seat => seat.SeatNo));
    }

    private static List<OwnedSeat> SeatsStillOwned(List<Dictionary<string, AttributeValue>> items, Guid bookingId)
    {
        string booking = bookingId.ToString("D");
        var seats = new List<OwnedSeat>();
        foreach (Dictionary<string, AttributeValue> item in items)
        {
            if (IsTripItem(item))
            {
                continue;
            }

            bool isThisBooking = item.ContainsKey("bookingId") && item["bookingId"].S == booking;
            string status = item["status"].S;
            bool isHeldOrBooked = status == nameof(SeatStatus.Held) || status == nameof(SeatStatus.Booked);
            if (isThisBooking && isHeldOrBooked)
            {
                seats.Add(new OwnedSeat(item["seatNo"].S, status));
            }
        }

        return seats;
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

    private TransactWriteItem ReleaseSeat(BookingCancelledMessage message, OwnedSeat seat, bool isTripCancelled, SeatChange change)
    {
        Dictionary<string, AttributeValue> values = ReleaseValues(message.BookingId!.Value, seat, change);
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
            Key = Key(message.TripId!.Value, SeatSortKeyPrefix + seat.SeatNo),
            UpdateExpression = updateExpression,
            ConditionExpression = "#status = :was AND bookingId = :booking",
            ExpressionAttributeNames = new Dictionary<string, string> { { "#status", "status" } },
            ExpressionAttributeValues = values,
        };
        return new TransactWriteItem { Update = update };
    }

    // The values every freed seat's update needs, whichever status it goes to.
    private static Dictionary<string, AttributeValue> ReleaseValues(Guid bookingId, OwnedSeat seat, SeatChange change)
    {
        return new Dictionary<string, AttributeValue>
        {
            { ":was", new AttributeValue { S = seat.Status } },
            { ":booking", new AttributeValue { S = bookingId.ToString("D") } },
            { ":reason", new AttributeValue { S = ChangeReason } },
            { ":request", new AttributeValue { S = change.Request } },
            { ":correlation", new AttributeValue { S = change.CorrelationId } },
            { ":now", new AttributeValue { S = change.ChangedAt } },
        };
    }

    // Data that does not even have BookingCancelled's shape gets null, and is reported rather than guessed at.
    private static BookingCancelledMessage? ReadMessage(JsonElement data)
    {
        try
        {
            return data.Deserialize<BookingCancelledMessage>(WireJson.Options);
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

    /// <summary>A seat of the booking as read: Held or Booked.</summary>
    /// <param name="SeatNo">The seat label.</param>
    /// <param name="Status">Held or Booked — the release requires it unchanged.</param>
    private sealed record OwnedSeat(string SeatNo, string Status);

    /// <summary>The change record every seat of one release shares.</summary>
    /// <param name="Request">The request identifier the seat-change stream groups by.</param>
    /// <param name="CorrelationId">The correlation identifier of the Booking request that cancelled the booking.</param>
    /// <param name="ChangedAt">When the seats were freed, ISO-8601 UTC.</param>
    private sealed record SeatChange(string Request, string CorrelationId, string ChangedAt);
}
