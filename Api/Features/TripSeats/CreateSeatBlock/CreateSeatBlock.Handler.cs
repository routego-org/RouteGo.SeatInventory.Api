namespace Api.Features.TripSeats.CreateSeatBlock;

/// <summary>
/// Blocks free seats of an operator's trip, all or none. The change is one conditional DynamoDB
/// transaction, so a seat that a customer holds a moment before can never be blocked, and every
/// changed seat records why, by which request and by whom — the seat-change stream turns that
/// record into the SeatBlocked event and the audit record.
/// </summary>
public sealed class CreateSeatBlockHandler
{
    private const string TripKeyPrefix = "TRIP#";
    private const string TripSortKey = "TRIP";
    private const string SeatSortKeyPrefix = "SEAT#";
    private const string Available = "Available";
    private const string Blocked = "Blocked";
    private const string OperatorBlockReason = "Operator";
    private const string CancellationBlock = "BlockedByCancellation";
    private const string ChangeReason = "OperatorBlocked";
    private const string ConditionFailed = "ConditionalCheckFailed";

    // ROW, COLUMN and STATUS are DynamoDB reserved words, hence the placeholders.
    private const string Projection = "sk, operatorId, departureAt, isCancelled, seatNo, #status, blockReason";

    private const string BlockSeatUpdate =
        "SET #status = :blocked, blockReason = :operator, lastChangeReason = :reason, "
        + "lastChangeRequest = :request, lastChangeCorrelationId = :correlation, lastChangedBy = :user, lastChangedAt = :now "
        + "REMOVE bookingId, holdExpiresAt, lastChangeNote, lastChangeMovedFrom";

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;
    private readonly IClock _clock;

    public CreateSeatBlockHandler(IAmazonDynamoDB dynamoDb, string tableName, IClock clock)
    {
        _dynamoDb = dynamoDb;
        _tableName = tableName;
        _clock = clock;
    }

    public async Task<CreateSeatBlockResult> HandleAsync(
        Guid tripId, CreateSeatBlockRequest request, SeatBlockCaller caller, CancellationToken cancellationToken)
    {
        List<string> seatNos = request.SeatNos!;
        DateTimeOffset now = _clock.UtcNow;

        List<Dictionary<string, AttributeValue>> items = await ListTripItemsAsync(tripId, cancellationToken);

        Dictionary<string, AttributeValue>? trip = items.Find(IsTripItem);
        CreateSeatBlockResult? tripRefusal = RefuseForTrip(trip, caller, now);
        if (tripRefusal != null)
        {
            return tripRefusal;
        }

        Dictionary<string, string> seatStates = ToSeatStates(items);
        CreateSeatBlockResult? seatRefusal = RefuseForSeats(seatNos, seatStates);
        if (seatRefusal != null)
        {
            return seatRefusal;
        }

        List<string> toBlock = seatNos.FindAll(seatNo => seatStates[seatNo] == Available);
        if (toBlock.Count == 0)
        {
            return ToResult(CreateSeatBlockOutcome.NothingToChange, tripId, seatNos, toBlock);
        }

        CreateSeatBlockResult? raceRefusal = await BlockAsync(tripId, toBlock, caller, now, cancellationToken);
        if (raceRefusal != null)
        {
            return raceRefusal;
        }

        return ToResult(CreateSeatBlockOutcome.Blocked, tripId, seatNos, toBlock);
    }

    private static CreateSeatBlockResult? RefuseForTrip(
        Dictionary<string, AttributeValue>? trip, SeatBlockCaller caller, DateTimeOffset now)
    {
        // A trip known only from an early cancellation has not been announced: not found.
        if (trip is null || !trip.ContainsKey("departureAt"))
        {
            return new CreateSeatBlockResult { Outcome = CreateSeatBlockOutcome.TripNotFound };
        }

        if (!string.Equals(trip["operatorId"].S, caller.OperatorId, StringComparison.OrdinalIgnoreCase))
        {
            return new CreateSeatBlockResult { Outcome = CreateSeatBlockOutcome.NotTheTripsOperator };
        }

        if (trip.ContainsKey("isCancelled") && trip["isCancelled"].BOOL == true)
        {
            return new CreateSeatBlockResult { Outcome = CreateSeatBlockOutcome.TripCancelled };
        }

        DateTimeOffset departureAt = ParseUtc(trip["departureAt"].S);
        if (departureAt <= now)
        {
            return new CreateSeatBlockResult { Outcome = CreateSeatBlockOutcome.TripDeparted, DepartedAt = departureAt };
        }

        return null;
    }

    // Each seat's state as blocking sees it: "Available", "Blocked" for an operator block, or
    // anything else for a seat that is not free — a cancellation block is never the operator's.
    private static Dictionary<string, string> ToSeatStates(List<Dictionary<string, AttributeValue>> items)
    {
        var seatStates = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Dictionary<string, AttributeValue> item in items)
        {
            if (IsTripItem(item))
            {
                continue;
            }

            string status = item["status"].S;
            bool isOperatorBlock = status == Blocked
                && item.ContainsKey("blockReason")
                && item["blockReason"].S == OperatorBlockReason;
            string state = status;
            if (status == Blocked && !isOperatorBlock)
            {
                state = CancellationBlock;
            }

            seatStates[item["seatNo"].S] = state;
        }

        return seatStates;
    }

    private static CreateSeatBlockResult? RefuseForSeats(List<string> seatNos, Dictionary<string, string> seatStates)
    {
        List<string> unknown = seatNos.FindAll(seatNo => !seatStates.ContainsKey(seatNo));
        if (unknown.Count > 0)
        {
            return new CreateSeatBlockResult { Outcome = CreateSeatBlockOutcome.UnknownSeats, RefusedSeats = unknown };
        }

        // Already blocked by the operator counts as done, which is what makes a repeat harmless.
        List<string> unavailable = seatNos.FindAll(seatNo => seatStates[seatNo] != Available && seatStates[seatNo] != Blocked);
        if (unavailable.Count > 0)
        {
            return new CreateSeatBlockResult { Outcome = CreateSeatBlockOutcome.SeatsUnavailable, RefusedSeats = unavailable };
        }

        return null;
    }

    private async Task<CreateSeatBlockResult?> BlockAsync(
        Guid tripId, List<string> toBlock, SeatBlockCaller caller, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Every seat this request changes shares one request identifier, so the seat-change stream
        // can group them back into one SeatBlocked event and one audit record.
        string changeRequest = Guid.NewGuid().ToString("D");
        var writes = new List<TransactWriteItem> { TripStillOpen(tripId) };
        foreach (string seatNo in toBlock)
        {
            writes.Add(BlockSeat(tripId, seatNo, changeRequest, caller, now));
        }

        try
        {
            await _dynamoDb.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = writes }, cancellationToken);
        }
        catch (TransactionCanceledException exception) when (HasConditionFailure(exception))
        {
            // Lost a race since the read: the trip was cancelled, or a seat was held or blocked meanwhile.
            return ToRaceRefusal(exception.CancellationReasons, toBlock);
        }

        return null;
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

    private TransactWriteItem BlockSeat(Guid tripId, string seatNo, string changeRequest, SeatBlockCaller caller, DateTimeOffset now)
    {
        return new TransactWriteItem
        {
            Update = new Update
            {
                TableName = _tableName,
                Key = Key(tripId, SeatSortKeyPrefix + seatNo),
                UpdateExpression = BlockSeatUpdate,
                ConditionExpression = "#status = :available",
                ExpressionAttributeNames = new Dictionary<string, string> { { "#status", "status" } },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    { ":available", new AttributeValue { S = Available } },
                    { ":blocked", new AttributeValue { S = Blocked } },
                    { ":operator", new AttributeValue { S = OperatorBlockReason } },
                    { ":reason", new AttributeValue { S = ChangeReason } },
                    { ":request", new AttributeValue { S = changeRequest } },
                    { ":correlation", new AttributeValue { S = caller.CorrelationId } },
                    { ":user", new AttributeValue { S = caller.UserId! } },
                    { ":now", new AttributeValue { S = now.UtcDateTime.ToString("O") } },
                },
            },
        };
    }

    private static bool HasConditionFailure(TransactionCanceledException exception)
    {
        if (exception.CancellationReasons is null)
        {
            return false;
        }

        return exception.CancellationReasons.Exists(reason => reason.Code == ConditionFailed);
    }

    // The reasons line up with the transaction's items: the trip check first, then one per seat.
    private static CreateSeatBlockResult ToRaceRefusal(List<CancellationReason> reasons, List<string> toBlock)
    {
        if (reasons[0].Code == ConditionFailed)
        {
            return new CreateSeatBlockResult { Outcome = CreateSeatBlockOutcome.TripCancelled };
        }

        var taken = new List<string>();
        for (int i = 0; i < toBlock.Count; i++)
        {
            if (reasons[i + 1].Code == ConditionFailed)
            {
                taken.Add(toBlock[i]);
            }
        }

        return new CreateSeatBlockResult { Outcome = CreateSeatBlockOutcome.SeatsUnavailable, RefusedSeats = taken };
    }

    private async Task<List<Dictionary<string, AttributeValue>>> ListTripItemsAsync(
        Guid tripId, CancellationToken cancellationToken)
    {
        var query = new QueryRequest
        {
            TableName = _tableName,
            KeyConditionExpression = "pk = :pk",
            ProjectionExpression = Projection,
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
        var parsed = DateTimeOffset.Parse(
            text,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal);
        return parsed.ToUniversalTime();
    }

    private static CreateSeatBlockResult ToResult(
        CreateSeatBlockOutcome outcome, Guid tripId, List<string> seatNos, List<string> blocked)
    {
        return new CreateSeatBlockResult
        {
            Outcome = outcome,
            Response = new CreateSeatBlockResponse(tripId, seatNos, blocked),
        };
    }
}
