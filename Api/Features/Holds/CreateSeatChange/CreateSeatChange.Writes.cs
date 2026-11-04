namespace Api.Features.Holds.CreateSeatChange;

/// <summary>
/// The items of a seat change's DynamoDB transaction: a check that the trip is still open, and per
/// move one conditional update freeing the old seat and one booking the new seat.
/// </summary>
internal static class CreateSeatChangeWrites
{
    private const string TripKeyPrefix = "TRIP#";
    private const string TripSortKey = "TRIP";
    private const string SeatSortKeyPrefix = "SEAT#";
    private const string Available = "Available";
    private const string Booked = "Booked";
    private const string ChangeReason = "SeatChanged";
    private const string ConditionFailed = "ConditionalCheckFailed";
    private const string TransactionConflict = "TransactionConflict";

    // STATUS is a DynamoDB reserved word, hence the placeholder.
    private const string FreeSeatUpdate =
        "SET #status = :available, lastChangeReason = :reason, lastChangeRequest = :request, "
        + "lastChangeCorrelationId = :correlation, lastChangedAt = :now "
        + "REMOVE bookingId, holdExpiresAt, blockReason, lastChangedBy, lastChangeNote, lastChangeMovedFrom";

    // The new seat names the seat it replaced, so the seat-change stream can pair each move back up.
    private const string BookSeatUpdate =
        "SET #status = :booked, bookingId = :booking, lastChangeReason = :reason, lastChangeRequest = :request, "
        + "lastChangeCorrelationId = :correlation, lastChangedAt = :now, lastChangeMovedFrom = :from "
        + "REMOVE holdExpiresAt, blockReason, lastChangedBy, lastChangeNote";

    /// <summary>The whole transaction: the trip check first, then both seats of every move still to do.</summary>
    public static List<TransactWriteItem> MoveSeats(
        string tableName, SeatChangeTarget change, List<SeatMove> pending, DateTimeOffset now)
    {
        // Every seat of the change shares one request identifier, so the seat-change stream can group
        // them back into one SeatChanged event.
        var record = new SeatChangeRecord(Guid.NewGuid().ToString("D"), change.CorrelationId, now.UtcDateTime.ToString("O"));
        var writes = new List<TransactWriteItem> { TripStillOpen(tableName, change.TripId) };
        foreach (SeatMove move in pending)
        {
            writes.Add(FreeSeat(tableName, change, move.FromSeatNo, record));
            writes.Add(BookSeat(tableName, change, move, record));
        }

        return writes;
    }

    /// <summary>
    /// True when the transaction was cancelled because another change got to a seat or the trip
    /// first — a condition failed, or a simultaneous transaction on the same item conflicted.
    /// </summary>
    public static bool IsLostRace(TransactionCanceledException exception)
    {
        if (exception.CancellationReasons is null)
        {
            return false;
        }

        return exception.CancellationReasons.Exists(reason => reason.Code == ConditionFailed || reason.Code == TransactionConflict);
    }

    private static TransactWriteItem TripStillOpen(string tableName, Guid tripId)
    {
        return new TransactWriteItem
        {
            ConditionCheck = new ConditionCheck
            {
                TableName = tableName,
                Key = Key(tripId, TripSortKey),
                ConditionExpression = "attribute_not_exists(isCancelled) OR isCancelled = :false",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    { ":false", new AttributeValue { BOOL = false } },
                },
            },
        };
    }

    private static TransactWriteItem FreeSeat(string tableName, SeatChangeTarget change, string seatNo, SeatChangeRecord record)
    {
        Dictionary<string, AttributeValue> values = RecordValues(record);
        values[":available"] = new AttributeValue { S = Available };
        values[":booked"] = new AttributeValue { S = Booked };
        values[":booking"] = new AttributeValue { S = change.BookingId.ToString("D") };
        var update = new Update
        {
            TableName = tableName,
            Key = Key(change.TripId, SeatSortKeyPrefix + seatNo),
            UpdateExpression = FreeSeatUpdate,
            ConditionExpression = "#status = :booked AND bookingId = :booking",
            ExpressionAttributeNames = new Dictionary<string, string> { { "#status", "status" } },
            ExpressionAttributeValues = values,
        };
        return new TransactWriteItem { Update = update };
    }

    private static TransactWriteItem BookSeat(string tableName, SeatChangeTarget change, SeatMove move, SeatChangeRecord record)
    {
        Dictionary<string, AttributeValue> values = RecordValues(record);
        values[":available"] = new AttributeValue { S = Available };
        values[":booked"] = new AttributeValue { S = Booked };
        values[":booking"] = new AttributeValue { S = change.BookingId.ToString("D") };
        values[":from"] = new AttributeValue { S = move.FromSeatNo };
        var update = new Update
        {
            TableName = tableName,
            Key = Key(change.TripId, SeatSortKeyPrefix + move.ToSeatNo),
            UpdateExpression = BookSeatUpdate,
            ConditionExpression = "#status = :available",
            ExpressionAttributeNames = new Dictionary<string, string> { { "#status", "status" } },
            ExpressionAttributeValues = values,
        };
        return new TransactWriteItem { Update = update };
    }

    private static Dictionary<string, AttributeValue> RecordValues(SeatChangeRecord record)
    {
        return new Dictionary<string, AttributeValue>
        {
            { ":reason", new AttributeValue { S = ChangeReason } },
            { ":request", new AttributeValue { S = record.Request } },
            { ":correlation", new AttributeValue { S = record.CorrelationId } },
            { ":now", new AttributeValue { S = record.ChangedAt } },
        };
    }

    private static Dictionary<string, AttributeValue> Key(Guid tripId, string sortKey)
    {
        return new Dictionary<string, AttributeValue>
        {
            { "pk", new AttributeValue { S = TripKeyPrefix + tripId.ToString("D") } },
            { "sk", new AttributeValue { S = sortKey } },
        };
    }

    /// <summary>The change record every seat of one seat change shares.</summary>
    /// <param name="Request">The request identifier the seat-change stream groups by.</param>
    /// <param name="CorrelationId">The request's correlation identifier.</param>
    /// <param name="ChangedAt">When the change was made, ISO-8601 UTC.</param>
    private sealed record SeatChangeRecord(string Request, string CorrelationId, string ChangedAt);
}
