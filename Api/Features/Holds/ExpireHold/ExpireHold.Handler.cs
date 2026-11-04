namespace Api.Features.Holds.ExpireHold;

/// <summary>
/// Frees a hold's seats when its timer fires, if they are still Held by that booking with that exact
/// hold expiry: back to Available, or to Blocked when the trip is cancelled, because a cancelled
/// trip's seats never go back on sale. Repeat-safe by its condition: the booking with its hold expiry
/// is the key, so a second delivery of the timer — or a timer whose hold was confirmed, released,
/// cancelled or held again with a new expiry — finds no such seat and changes nothing. The change is
/// one conditional DynamoDB transaction, and every freed seat records the change the seat-change
/// stream turns into SeatReleased with reason HoldExpired.
/// </summary>
public sealed class ExpireHoldHandler
{
    private const string TripKeyPrefix = "TRIP#";
    private const string TripSortKey = "TRIP";
    private const string SeatSortKeyPrefix = "SEAT#";
    private const string Available = "Available";
    private const string Held = "Held";
    private const string Blocked = "Blocked";
    private const string TripCancelledBlock = "TripCancelled";
    private const string ChangeReason = "HoldExpired";
    private const string ConditionFailed = "ConditionalCheckFailed";
    private const string TransactionConflict = "TransactionConflict";

    // An expiry can lose a race with a confirm, a release or the trip's cancellation; a few attempts,
    // each after a fresh read, settle it on what the seats and the trip are now.
    private const int MaxWriteAttempts = 3;

    // STATUS is a DynamoDB reserved word, hence the placeholder.
    private const string Projection = "sk, isCancelled, seatNo, #status, bookingId, holdExpiresAt";

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

    public ExpireHoldHandler(IAmazonDynamoDB dynamoDb, IConfiguration configuration, IClock clock)
    {
        _dynamoDb = dynamoDb;
        // Present by the time any timer arrives: startup refuses to run without it.
        _tableName = configuration[Infrastructure.ConfigKeys.SeatStoreTableName]!;
        _clock = clock;
    }

    /// <summary>Frees whatever the timer's hold still holds; the seats freed, possibly none.</summary>
    public async Task<ExpireHoldResult> HandleAsync(HoldExpiryTimerMessage timer, CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= MaxWriteAttempts; attempt++)
        {
            ExpireHoldResult? result = await TryExpireAsync(timer, cancellationToken);
            if (result != null)
            {
                return result;
            }
        }

        // Nothing was changed, and an expiry is safe to repeat, so the queue's next delivery settles it.
        throw new InvalidOperationException(
            $"Expiring booking '{timer.BookingId}' on trip '{timer.TripId}' lost {MaxWriteAttempts} races in a row; nothing changed.");
    }

    // Null means the write lost a race since the read, and the expiry should be decided again.
    private async Task<ExpireHoldResult?> TryExpireAsync(HoldExpiryTimerMessage timer, CancellationToken cancellationToken)
    {
        List<Dictionary<string, AttributeValue>> items = await ListTripItemsAsync(timer.TripId, cancellationToken);
        Dictionary<string, AttributeValue>? trip = items.Find(IsTripItem);
        List<string> stillHeld = SeatsStillHeld(items, timer);
        if (trip is null || stillHeld.Count == 0)
        {
            return new ExpireHoldResult(Array.Empty<string>());
        }

        bool isExpired = await TryWriteAsync(timer, stillHeld, IsCancelled(trip), cancellationToken);
        if (!isExpired)
        {
            return null;
        }

        return new ExpireHoldResult(stillHeld);
    }

    private static bool IsCancelled(Dictionary<string, AttributeValue> trip)
    {
        return trip.ContainsKey("isCancelled") && trip["isCancelled"].BOOL == true;
    }

    // Only the seats of this very hold: Held, by this booking, with this hold expiry.
    private static List<string> SeatsStillHeld(List<Dictionary<string, AttributeValue>> items, HoldExpiryTimerMessage timer)
    {
        string bookingId = timer.BookingId.ToString("D");
        var seatNos = new List<string>();
        foreach (Dictionary<string, AttributeValue> item in items)
        {
            bool isThisHold = !IsTripItem(item) && TextOf(item, "bookingId") == bookingId && TextOf(item, "holdExpiresAt") == timer.HoldExpiresAt;
            if (isThisHold && item["status"].S == Held)
            {
                seatNos.Add(item["seatNo"].S);
            }
        }

        return seatNos;
    }

    private static string? TextOf(Dictionary<string, AttributeValue> item, string name)
    {
        if (!item.ContainsKey(name))
        {
            return null;
        }

        return item[name].S;
    }

    private async Task<bool> TryWriteAsync(
        HoldExpiryTimerMessage timer, List<string> stillHeld, bool isTripCancelled, CancellationToken cancellationToken)
    {
        // Every seat of the hold shares one request identifier, so the seat-change stream can group
        // them back into one SeatReleased event.
        var change = new SeatChange(Guid.NewGuid().ToString("D"), _clock.UtcNow.UtcDateTime.ToString("O"));
        var writes = new List<TransactWriteItem>();

        // Going back on sale is safe only while the trip is still open; a cancellation landing
        // meanwhile fails this check, and the next attempt blocks the seats instead.
        if (!isTripCancelled)
        {
            writes.Add(TripStillOpen(timer.TripId));
        }

        foreach (string seatNo in stillHeld)
        {
            writes.Add(ReleaseSeat(timer, seatNo, isTripCancelled, change));
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

    private TransactWriteItem ReleaseSeat(HoldExpiryTimerMessage timer, string seatNo, bool isTripCancelled, SeatChange change)
    {
        Dictionary<string, AttributeValue> values = ReleaseValues(timer, change);
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
            Key = Key(timer.TripId, SeatSortKeyPrefix + seatNo),
            UpdateExpression = updateExpression,
            ConditionExpression = "#status = :held AND bookingId = :booking AND holdExpiresAt = :expiry",
            ExpressionAttributeNames = new Dictionary<string, string> { { "#status", "status" } },
            ExpressionAttributeValues = values,
        };
        return new TransactWriteItem { Update = update };
    }

    // The values every freed seat's update needs, whichever status it goes to.
    private static Dictionary<string, AttributeValue> ReleaseValues(HoldExpiryTimerMessage timer, SeatChange change)
    {
        return new Dictionary<string, AttributeValue>
        {
            { ":held", new AttributeValue { S = Held } },
            { ":booking", new AttributeValue { S = timer.BookingId.ToString("D") } },
            { ":expiry", new AttributeValue { S = timer.HoldExpiresAt } },
            { ":reason", new AttributeValue { S = ChangeReason } },
            { ":request", new AttributeValue { S = change.Request } },
            { ":correlation", new AttributeValue { S = timer.CorrelationId } },
            { ":now", new AttributeValue { S = change.ChangedAt } },
        };
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

    /// <summary>The change record every seat of one expiry shares.</summary>
    /// <param name="Request">The request identifier the seat-change stream groups by.</param>
    /// <param name="ChangedAt">When the change was made, ISO-8601 UTC.</param>
    private sealed record SeatChange(string Request, string ChangedAt);
}
