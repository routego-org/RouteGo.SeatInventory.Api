namespace Api.Features.Holds.ConfirmHold;

/// <summary>
/// Books every seat a booking holds on a trip, together. The change is one conditional DynamoDB
/// transaction — each seat only while it is still Held by this booking — so a confirm and the hold
/// timer racing for the same seats can never both win, and every booked seat records the change
/// the seat-change stream turns into the SeatBooked event.
/// </summary>
public sealed class ConfirmHoldHandler
{
    private const string TripKeyPrefix = "TRIP#";
    private const string TripSortKey = "TRIP";
    private const string SeatSortKeyPrefix = "SEAT#";
    private const string Held = "Held";
    private const string Booked = "Booked";
    private const string ChangeReason = "Booked";
    private const string ConditionFailed = "ConditionalCheckFailed";
    private const string TransactionConflict = "TransactionConflict";

    // A confirm can lose a race with the hold timer or a release; a few attempts, each after a fresh
    // read, settle it on what the seats are now.
    private const int MaxWriteAttempts = 3;

    // STATUS is a DynamoDB reserved word, hence the placeholder.
    private const string Projection = "sk, departureAt, isCancelled, seatNo, #status, bookingId";

    private const string BookSeatUpdate =
        "SET #status = :booked, lastChangeReason = :reason, lastChangeRequest = :request, "
        + "lastChangeCorrelationId = :correlation, lastChangedAt = :now "
        + "REMOVE holdExpiresAt, blockReason, lastChangedBy, lastChangeNote";

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;
    private readonly IClock _clock;

    public ConfirmHoldHandler(IAmazonDynamoDB dynamoDb, string tableName, IClock clock)
    {
        _dynamoDb = dynamoDb;
        _tableName = tableName;
        _clock = clock;
    }

    public async Task<ConfirmHoldResult> HandleAsync(
        ConfirmHoldRequest request, string correlationId, CancellationToken cancellationToken)
    {
        var target = new ConfirmTarget(Guid.Parse(request.TripId), Guid.Parse(request.BookingId), correlationId);
        for (int attempt = 1; attempt <= MaxWriteAttempts; attempt++)
        {
            ConfirmHoldResult? result = await TryConfirmAsync(target, cancellationToken);
            if (result != null)
            {
                return result;
            }
        }

        // Nothing was changed, and a confirm is safe to repeat, so Booking's retry settles it.
        throw new InvalidOperationException(
            $"Confirming booking '{target.BookingId}' on trip '{target.TripId}' lost {MaxWriteAttempts} races in a row; nothing changed.");
    }

    // Null means the write lost a race since the read, and the confirm should be decided again.
    private async Task<ConfirmHoldResult?> TryConfirmAsync(ConfirmTarget target, CancellationToken cancellationToken)
    {
        List<Dictionary<string, AttributeValue>> items = await ListTripItemsAsync(target.TripId, cancellationToken);
        ConfirmHoldResult? refusal = RefuseForTrip(items.Find(IsTripItem));
        if (refusal != null)
        {
            return refusal;
        }

        string bookingId = target.BookingId.ToString("D");
        List<Dictionary<string, AttributeValue>> owned = items.FindAll(item => IsHeldOrBookedBy(item, bookingId));
        if (owned.Count == 0)
        {
            return new ConfirmHoldResult(ConfirmHoldOutcome.HoldNotActive, null);
        }

        // Seats already booked by the booking count as confirmed, which is what makes a repeat harmless.
        List<string> toBook = owned.FindAll(seat => seat["status"].S == Held).ConvertAll(seat => seat["seatNo"].S);
        if (toBook.Count > 0)
        {
            bool isBooked = await TryBookAsync(target, toBook, cancellationToken);
            if (!isBooked)
            {
                return null;
            }
        }

        List<string> seatNos = owned.ConvertAll(seat => seat["seatNo"].S);
        return new ConfirmHoldResult(ConfirmHoldOutcome.Confirmed, ToResponse(target, seatNos));
    }

    private static ConfirmHoldResult? RefuseForTrip(Dictionary<string, AttributeValue>? trip)
    {
        // A trip known only from an early cancellation has not been announced: not found.
        if (trip is null || !trip.ContainsKey("departureAt"))
        {
            return new ConfirmHoldResult(ConfirmHoldOutcome.TripNotFound, null);
        }

        // A confirm is never refused for departure: a customer may finish paying after the bus left.
        if (trip.ContainsKey("isCancelled") && trip["isCancelled"].BOOL == true)
        {
            return new ConfirmHoldResult(ConfirmHoldOutcome.TripCancelled, null);
        }

        return null;
    }

    private static bool IsHeldOrBookedBy(Dictionary<string, AttributeValue> item, string bookingId)
    {
        if (IsTripItem(item) || !item.ContainsKey("bookingId") || item["bookingId"].S != bookingId)
        {
            return false;
        }

        string status = item["status"].S;
        return status == Held || status == Booked;
    }

    private async Task<bool> TryBookAsync(ConfirmTarget target, List<string> toBook, CancellationToken cancellationToken)
    {
        // Every seat of the hold shares one request identifier, so the seat-change stream can group
        // them back into one SeatBooked event.
        string changeRequest = Guid.NewGuid().ToString("D");
        string now = _clock.UtcNow.UtcDateTime.ToString("O");
        var writes = new List<TransactWriteItem> { TripStillOpen(target.TripId) };
        foreach (string seatNo in toBook)
        {
            writes.Add(BookSeat(target, seatNo, changeRequest, now));
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

    private TransactWriteItem BookSeat(ConfirmTarget target, string seatNo, string changeRequest, string now)
    {
        return new TransactWriteItem
        {
            Update = new Update
            {
                TableName = _tableName,
                Key = Key(target.TripId, SeatSortKeyPrefix + seatNo),
                UpdateExpression = BookSeatUpdate,
                ConditionExpression = "#status = :held AND bookingId = :booking",
                ExpressionAttributeNames = new Dictionary<string, string> { { "#status", "status" } },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    { ":held", new AttributeValue { S = Held } },
                    { ":booking", new AttributeValue { S = target.BookingId.ToString("D") } },
                    { ":booked", new AttributeValue { S = Booked } },
                    { ":reason", new AttributeValue { S = ChangeReason } },
                    { ":request", new AttributeValue { S = changeRequest } },
                    { ":correlation", new AttributeValue { S = target.CorrelationId } },
                    { ":now", new AttributeValue { S = now } },
                },
            },
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

    private static ConfirmHoldResponse ToResponse(ConfirmTarget target, List<string> seatNos)
    {
        return new ConfirmHoldResponse
        {
            TripId = target.TripId,
            BookingId = target.BookingId,
            SeatNos = seatNos,
            Status = HoldStatus.Booked,
            HoldExpiresAt = null,
        };
    }

    /// <summary>The hold this request confirms.</summary>
    /// <param name="TripId">The trip.</param>
    /// <param name="BookingId">The booking.</param>
    /// <param name="CorrelationId">The confirm request's correlation identifier, stored on every booked seat.</param>
    private sealed record ConfirmTarget(Guid TripId, Guid BookingId, string CorrelationId);
}
