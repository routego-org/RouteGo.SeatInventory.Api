namespace Api.Features.TripSeats.DeleteSeatBlock;

/// <summary>
/// Puts seats an operator blocked back on sale, all or none. The change is one conditional DynamoDB
/// transaction — each seat only while it is still blocked by the operator, and only while the trip
/// is not cancelled, so a cancellation block is never lifted — and every changed seat records why,
/// by which request and by whom, which the seat-change stream turns into SeatUnblocked and the
/// audit record.
/// </summary>
public sealed class DeleteSeatBlockHandler
{
    private const string TripKeyPrefix = "TRIP#";
    private const string TripSortKey = "TRIP";
    private const string SeatSortKeyPrefix = "SEAT#";
    private const string Available = "Available";
    private const string Blocked = "Blocked";
    private const string OperatorBlockReason = "Operator";
    private const string ChangeReason = "OperatorUnblocked";
    private const string ConditionFailed = "ConditionalCheckFailed";
    private const string TransactionConflict = "TransactionConflict";

    // An unblock can lose a race with the trip's cancellation or another unblock; a few attempts,
    // each after a fresh read, settle it on what the seats and the trip are now.
    private const int MaxWriteAttempts = 3;

    // STATUS is a DynamoDB reserved word, hence the placeholder.
    private const string Projection = "sk, operatorId, departureAt, isCancelled, seatNo, #status, blockReason";

    private const string UnblockSeatUpdate =
        "SET #status = :available, lastChangeReason = :reason, lastChangeRequest = :request, "
        + "lastChangeCorrelationId = :correlation, lastChangedBy = :user, lastChangedAt = :now "
        + "REMOVE blockReason, bookingId, holdExpiresAt, lastChangeNote, lastChangeMovedFrom";

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;
    private readonly IClock _clock;

    public DeleteSeatBlockHandler(IAmazonDynamoDB dynamoDb, string tableName, IClock clock)
    {
        _dynamoDb = dynamoDb;
        _tableName = tableName;
        _clock = clock;
    }

    public async Task<DeleteSeatBlockResult> HandleAsync(SeatUnblockTarget unblock, CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= MaxWriteAttempts; attempt++)
        {
            DeleteSeatBlockResult? result = await TryUnblockAsync(unblock, cancellationToken);
            if (result != null)
            {
                return result;
            }
        }

        // Nothing was changed, and an unblock is safe to repeat, so the operator can simply try again.
        throw new InvalidOperationException(
            $"Unblocking seats on trip '{unblock.TripId}' lost {MaxWriteAttempts} races in a row; nothing changed.");
    }

    // Null means the write lost a race since the read, and the unblock should be decided again.
    private async Task<DeleteSeatBlockResult?> TryUnblockAsync(SeatUnblockTarget unblock, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;
        List<Dictionary<string, AttributeValue>> items = await ListTripItemsAsync(unblock.TripId, cancellationToken);
        DeleteSeatBlockResult? tripRefusal = RefuseForTrip(items.Find(IsTripItem), unblock.OperatorId, now);
        if (tripRefusal != null)
        {
            return tripRefusal;
        }

        Dictionary<string, string> seatStates = ToSeatStates(items);
        DeleteSeatBlockResult? seatRefusal = RefuseForSeats(unblock.SeatNos, seatStates);
        if (seatRefusal != null)
        {
            return seatRefusal;
        }

        // Already Available counts as done, which is what makes a repeat harmless.
        List<string> toUnblock = unblock.SeatNos.FindAll(seatNo => seatStates[seatNo] == Blocked);
        if (toUnblock.Count > 0)
        {
            bool isUnblocked = await TryWriteAsync(unblock, toUnblock, now, cancellationToken);
            if (!isUnblocked)
            {
                return null;
            }
        }

        return new DeleteSeatBlockResult { Outcome = DeleteSeatBlockOutcome.Unblocked };
    }

    private static DeleteSeatBlockResult? RefuseForTrip(
        Dictionary<string, AttributeValue>? trip, string? operatorId, DateTimeOffset now)
    {
        // A trip known only from an early cancellation has not been announced: not found.
        if (trip is null || !trip.ContainsKey("departureAt"))
        {
            return new DeleteSeatBlockResult { Outcome = DeleteSeatBlockOutcome.TripNotFound };
        }

        if (!string.Equals(trip["operatorId"].S, operatorId, StringComparison.OrdinalIgnoreCase))
        {
            return new DeleteSeatBlockResult { Outcome = DeleteSeatBlockOutcome.NotTheTripsOperator };
        }

        if (trip.ContainsKey("isCancelled") && trip["isCancelled"].BOOL == true)
        {
            return new DeleteSeatBlockResult { Outcome = DeleteSeatBlockOutcome.TripCancelled };
        }

        DateTimeOffset departureAt = ParseUtc(trip["departureAt"].S);
        if (departureAt <= now)
        {
            return new DeleteSeatBlockResult { Outcome = DeleteSeatBlockOutcome.TripDeparted, DepartedAt = departureAt };
        }

        return null;
    }

    // Each seat's state as unblocking sees it: "Available", "Blocked" for an operator block, or its
    // own status otherwise — a cancellation block is never the operator's, so it reads "TripCancelled".
    private static Dictionary<string, string> ToSeatStates(List<Dictionary<string, AttributeValue>> items)
    {
        var seatStates = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Dictionary<string, AttributeValue> item in items)
        {
            if (IsTripItem(item))
            {
                continue;
            }

            // A Blocked seat always carries its block reason.
            string state = item["status"].S;
            bool isOtherBlock = state == Blocked && item["blockReason"].S != OperatorBlockReason;
            if (isOtherBlock)
            {
                state = item["blockReason"].S;
            }

            seatStates[item["seatNo"].S] = state;
        }

        return seatStates;
    }

    private static DeleteSeatBlockResult? RefuseForSeats(List<string> seatNos, Dictionary<string, string> seatStates)
    {
        List<string> unknown = seatNos.FindAll(seatNo => !seatStates.ContainsKey(seatNo));
        if (unknown.Count > 0)
        {
            return new DeleteSeatBlockResult { Outcome = DeleteSeatBlockOutcome.UnknownSeats, RefusedSeats = unknown };
        }

        List<string> notOperatorBlocked = seatNos.FindAll(seatNo => seatStates[seatNo] != Available && seatStates[seatNo] != Blocked);
        if (notOperatorBlocked.Count > 0)
        {
            return new DeleteSeatBlockResult
            {
                Outcome = DeleteSeatBlockOutcome.SeatsNotOperatorBlocked,
                RefusedSeats = notOperatorBlocked,
            };
        }

        return null;
    }

    private async Task<bool> TryWriteAsync(
        SeatUnblockTarget unblock, List<string> toUnblock, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Every seat this request changes shares one request identifier, so the seat-change stream
        // can group them back into one SeatUnblocked event and one audit record.
        string changeRequest = Guid.NewGuid().ToString("D");
        var writes = new List<TransactWriteItem> { TripStillOpen(unblock.TripId) };
        foreach (string seatNo in toUnblock)
        {
            writes.Add(UnblockSeat(unblock, seatNo, changeRequest, now));
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

    private TransactWriteItem UnblockSeat(SeatUnblockTarget unblock, string seatNo, string changeRequest, DateTimeOffset now)
    {
        var update = new Update
        {
            TableName = _tableName,
            Key = Key(unblock.TripId, SeatSortKeyPrefix + seatNo),
            UpdateExpression = UnblockSeatUpdate,
            ConditionExpression = "#status = :blocked AND blockReason = :operator",
            ExpressionAttributeNames = new Dictionary<string, string> { { "#status", "status" } },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":available", new AttributeValue { S = Available } },
                { ":blocked", new AttributeValue { S = Blocked } },
                { ":operator", new AttributeValue { S = OperatorBlockReason } },
                { ":reason", new AttributeValue { S = ChangeReason } },
                { ":request", new AttributeValue { S = changeRequest } },
                { ":correlation", new AttributeValue { S = unblock.CorrelationId } },
                { ":user", new AttributeValue { S = unblock.UserId } },
                { ":now", new AttributeValue { S = now.UtcDateTime.ToString("O") } },
            },
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

    private static DateTimeOffset ParseUtc(string text)
    {
        System.Globalization.DateTimeStyles styles = System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal;
        return DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture, styles);
    }
}
