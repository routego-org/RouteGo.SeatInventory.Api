namespace Api.Features.Holds.CancelHold;

/// <summary>
/// Frees every seat a booking holds on a trip, on an admin's word: back to Available, or to Blocked
/// when the trip is cancelled, because a cancelled trip's seats never go back on sale. The change is
/// one conditional DynamoDB transaction — each seat only while it is still Held by this booking — and
/// every released seat records the admin and the note, which the seat-change stream turns into
/// SeatReleased and the audit record.
/// </summary>
public sealed class CancelHoldHandler
{
    private const string TripKeyPrefix = "TRIP#";
    private const string TripSortKey = "TRIP";
    private const string SeatSortKeyPrefix = "SEAT#";
    private const string Available = "Available";
    private const string Held = "Held";
    private const string Booked = "Booked";
    private const string Blocked = "Blocked";
    private const string TripCancelledBlock = "TripCancelled";
    private const string ChangeReason = "AdminReleased";
    private const string ConditionFailed = "ConditionalCheckFailed";
    private const string TransactionConflict = "TransactionConflict";

    // A cancellation can lose a race with the hold timer, a confirm or the trip's cancellation; a
    // few attempts, each after a fresh read, settle it on what the seats and the trip are now.
    private const int MaxWriteAttempts = 3;

    // STATUS is a DynamoDB reserved word, hence the placeholder.
    private const string Projection = "sk, departureAt, isCancelled, seatNo, #status, bookingId";

    private const string ReleaseToAvailable =
        "SET #status = :available, lastChangeReason = :reason, lastChangeRequest = :request, "
        + "lastChangeCorrelationId = :correlation, lastChangedBy = :admin, lastChangeNote = :note, lastChangedAt = :now "
        + "REMOVE bookingId, holdExpiresAt, blockReason";

    private const string ReleaseToBlocked =
        "SET #status = :blocked, blockReason = :tripCancelled, lastChangeReason = :reason, lastChangeRequest = :request, "
        + "lastChangeCorrelationId = :correlation, lastChangedBy = :admin, lastChangeNote = :note, lastChangedAt = :now "
        + "REMOVE bookingId, holdExpiresAt";

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;
    private readonly IClock _clock;

    public CancelHoldHandler(IAmazonDynamoDB dynamoDb, string tableName, IClock clock)
    {
        _dynamoDb = dynamoDb;
        _tableName = tableName;
        _clock = clock;
    }

    public async Task<CancelHoldResult> HandleAsync(HoldToCancel hold, CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= MaxWriteAttempts; attempt++)
        {
            CancelHoldResult? result = await TryCancelAsync(hold, cancellationToken);
            if (result != null)
            {
                return result;
            }
        }

        // Nothing was changed, so the admin can simply try again.
        throw new InvalidOperationException(
            $"Cancelling booking '{hold.BookingId}' on trip '{hold.TripId}' lost {MaxWriteAttempts} races in a row; nothing changed.");
    }

    // Null means the write lost a race since the read, and the cancellation should be decided again.
    private async Task<CancelHoldResult?> TryCancelAsync(HoldToCancel hold, CancellationToken cancellationToken)
    {
        List<Dictionary<string, AttributeValue>> items = await ListTripItemsAsync(hold.TripId, cancellationToken);
        Dictionary<string, AttributeValue>? trip = items.Find(IsTripItem);
        if (trip is null || !IsAnnounced(trip))
        {
            return new CancelHoldResult { Outcome = CancelHoldOutcome.TripNotFound };
        }

        string bookingId = hold.BookingId.ToString("D");
        List<string> booked = SeatsOf(items, bookingId, Booked);
        if (booked.Count > 0)
        {
            return new CancelHoldResult { Outcome = CancelHoldOutcome.HoldAlreadyConfirmed, BookedSeats = booked };
        }

        // Unlike Booking's release, nothing to free is a refusal: the admin asked about a hold that
        // is not there, and no audit record may say otherwise.
        List<string> held = SeatsOf(items, bookingId, Held);
        if (held.Count == 0)
        {
            return new CancelHoldResult { Outcome = CancelHoldOutcome.HoldNotActive };
        }

        bool isReleased = await TryWriteAsync(hold, held, IsCancelled(trip), cancellationToken);
        if (!isReleased)
        {
            return null;
        }

        var response = new CancelHoldResponse(hold.TripId, hold.BookingId, held);
        return new CancelHoldResult { Outcome = CancelHoldOutcome.Cancelled, Response = response };
    }

    // A trip known only from an early cancellation has not been announced, so it is not found.
    private static bool IsAnnounced(Dictionary<string, AttributeValue> trip)
    {
        return trip.ContainsKey("departureAt");
    }

    private static bool IsCancelled(Dictionary<string, AttributeValue> trip)
    {
        return trip.ContainsKey("isCancelled") && trip["isCancelled"].BOOL == true;
    }

    private static List<string> SeatsOf(List<Dictionary<string, AttributeValue>> items, string bookingId, string status)
    {
        var seatNos = new List<string>();
        foreach (Dictionary<string, AttributeValue> item in items)
        {
            bool isBookings = !IsTripItem(item) && item.ContainsKey("bookingId") && item["bookingId"].S == bookingId;
            if (isBookings && item["status"].S == status)
            {
                seatNos.Add(item["seatNo"].S);
            }
        }

        return seatNos;
    }

    private async Task<bool> TryWriteAsync(
        HoldToCancel hold, List<string> held, bool isTripCancelled, CancellationToken cancellationToken)
    {
        // Every seat of the hold shares one request identifier, so the seat-change stream can group
        // them back into one SeatReleased event and one audit record.
        string changeRequest = Guid.NewGuid().ToString("D");
        string now = _clock.UtcNow.UtcDateTime.ToString("O");
        var writes = new List<TransactWriteItem>();

        // Going back on sale is safe only while the trip is still open; a cancellation landing
        // meanwhile fails this check, and the next attempt blocks the seats instead.
        if (!isTripCancelled)
        {
            writes.Add(TripStillOpen(hold.TripId));
        }

        foreach (string seatNo in held)
        {
            writes.Add(ReleaseSeat(hold, seatNo, isTripCancelled, new SeatChange(changeRequest, now)));
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

    private TransactWriteItem ReleaseSeat(HoldToCancel hold, string seatNo, bool isTripCancelled, SeatChange change)
    {
        var values = new Dictionary<string, AttributeValue>
        {
            { ":held", new AttributeValue { S = Held } },
            { ":booking", new AttributeValue { S = hold.BookingId.ToString("D") } },
            { ":reason", new AttributeValue { S = ChangeReason } },
            { ":request", new AttributeValue { S = change.Request } },
            { ":correlation", new AttributeValue { S = hold.CorrelationId } },
            { ":admin", new AttributeValue { S = hold.AdminUserId } },
            { ":note", new AttributeValue { S = hold.Note } },
            { ":now", new AttributeValue { S = change.ChangedAt } },
        };
        string updateExpression = ReleaseToAvailable;
        if (isTripCancelled)
        {
            updateExpression = ReleaseToBlocked;
            values[":blocked"] = new AttributeValue { S = Blocked };
            values[":tripCancelled"] = new AttributeValue { S = TripCancelledBlock };
        }
        else
        {
            values[":available"] = new AttributeValue { S = Available };
        }

        var update = new Update
        {
            TableName = _tableName,
            Key = Key(hold.TripId, SeatSortKeyPrefix + seatNo),
            UpdateExpression = updateExpression,
            ConditionExpression = "#status = :held AND bookingId = :booking",
            ExpressionAttributeNames = new Dictionary<string, string> { { "#status", "status" } },
            ExpressionAttributeValues = values,
        };
        return new TransactWriteItem { Update = update };
    }

    private static bool IsLostRace(TransactionCanceledException exception)
    {
        if (exception.CancellationReasons is null)
        {
            return false;
        }

        return exception.CancellationReasons.Exists(reason => reason.Code == ConditionFailed || reason.Code == TransactionConflict);
    }

    private async Task<List<Dictionary<string, AttributeValue>>> ListTripItemsAsync(
        Guid tripId, CancellationToken cancellationToken)
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

    private static Dictionary<string, AttributeValue> Key(Guid tripId, string sortKey)
    {
        return new Dictionary<string, AttributeValue>
        {
            { "pk", new AttributeValue { S = TripKeyPrefix + tripId.ToString("D") } },
            { "sk", new AttributeValue { S = sortKey } },
        };
    }

    private static bool IsTripItem(Dictionary<string, AttributeValue> item)
    {
        return item["sk"].S == TripSortKey;
    }

    /// <summary>The change record every seat of one cancellation shares.</summary>
    /// <param name="Request">The request identifier the seat-change stream groups by.</param>
    /// <param name="ChangedAt">When the change was made, ISO-8601 UTC.</param>
    private sealed record SeatChange(string Request, string ChangedAt);
}
