namespace Api.Features.Holds.CreateHold;

/// <summary>
/// The items of a hold's DynamoDB transaction: a check that the trip is still open, and one
/// conditional update per seat that succeeds only while the seat is Available.
/// </summary>
internal static class CreateHoldWrites
{
    private const string TripKeyPrefix = "TRIP#";
    private const string TripSortKey = "TRIP";
    private const string SeatSortKeyPrefix = "SEAT#";
    private const string Available = "Available";
    private const string Held = "Held";
    private const string ChangeReason = "Held";
    private const string ConditionFailed = "ConditionalCheckFailed";
    private const string TransactionConflict = "TransactionConflict";

    // STATUS is a DynamoDB reserved word, hence the placeholder.
    private const string HoldSeatUpdate =
        "SET #status = :held, bookingId = :booking, holdExpiresAt = :expiry, lastChangeReason = :reason, "
        + "lastChangeRequest = :request, lastChangeCorrelationId = :correlation, lastChangedAt = :now "
        + "REMOVE blockReason, lastChangedBy, lastChangeNote";

    /// <summary>The whole transaction: the trip check first, then one update per seat of the hold.</summary>
    public static List<TransactWriteItem> HoldSeats(string tableName, CreateHoldAttempt hold)
    {
        // Every seat of the hold shares one request identifier, so the seat-change stream can group
        // them back into one SeatHeld event.
        string changeRequest = Guid.NewGuid().ToString("D");
        var writes = new List<TransactWriteItem> { TripStillOpen(tableName, hold.TripId) };
        foreach (string seatNo in hold.SeatNos)
        {
            writes.Add(HoldSeat(tableName, hold, seatNo, changeRequest));
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

    private static TransactWriteItem HoldSeat(string tableName, CreateHoldAttempt hold, string seatNo, string changeRequest)
    {
        return new TransactWriteItem
        {
            Update = new Update
            {
                TableName = tableName,
                Key = Key(hold.TripId, SeatSortKeyPrefix + seatNo),
                UpdateExpression = HoldSeatUpdate,
                ConditionExpression = "#status = :available",
                ExpressionAttributeNames = new Dictionary<string, string> { { "#status", "status" } },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    { ":available", new AttributeValue { S = Available } },
                    { ":held", new AttributeValue { S = Held } },
                    { ":booking", new AttributeValue { S = hold.BookingId.ToString("D") } },
                    { ":expiry", new AttributeValue { S = hold.ExpiresAtText } },
                    { ":reason", new AttributeValue { S = ChangeReason } },
                    { ":request", new AttributeValue { S = changeRequest } },
                    { ":correlation", new AttributeValue { S = hold.CorrelationId } },
                    { ":now", new AttributeValue { S = hold.Now.UtcDateTime.ToString("O") } },
                },
            },
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
}
