namespace Api.Features.Holds.CreateSeatChange;

/// <summary>
/// Moves a booking from some of its Booked seats to Available seats of the same class, all together
/// or none. The change is one conditional DynamoDB transaction, so a new seat taken a moment before
/// moves nothing and the customer keeps the seats they had; every changed seat records the change,
/// and each new seat the seat it replaced, which the seat-change stream turns into SeatChanged.
/// </summary>
public sealed class CreateSeatChangeHandler
{
    private const string TripKeyPrefix = "TRIP#";
    private const string TripSortKey = "TRIP";
    private const string Available = "Available";
    private const string Booked = "Booked";

    // A change can lose a race with a hold, a block or the trip's cancellation; a few attempts, each
    // after a fresh read, settle it on what the seats and the trip are now.
    private const int MaxWriteAttempts = 3;

    // STATUS is a DynamoDB reserved word, hence the placeholder.
    private const string Projection = "sk, departureAt, isCancelled, seatNo, #status, bookingId, seatClass";

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;
    private readonly IClock _clock;

    public CreateSeatChangeHandler(IAmazonDynamoDB dynamoDb, string tableName, IClock clock)
    {
        _dynamoDb = dynamoDb;
        _tableName = tableName;
        _clock = clock;
    }

    public async Task<CreateSeatChangeResult> HandleAsync(SeatChangeTarget change, CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= MaxWriteAttempts; attempt++)
        {
            CreateSeatChangeResult? result = await TryChangeAsync(change, cancellationToken);
            if (result != null)
            {
                return result;
            }
        }

        // Nothing was changed, and a seat change is safe to repeat, so Booking's retry settles it.
        throw new InvalidOperationException(
            $"Changing seats of booking '{change.BookingId}' on trip '{change.TripId}' lost {MaxWriteAttempts} races in a row; nothing changed.");
    }

    // Null means the write lost a race since the read, and the change should be decided again.
    private async Task<CreateSeatChangeResult?> TryChangeAsync(SeatChangeTarget change, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;
        List<Dictionary<string, AttributeValue>> items = await ListTripItemsAsync(change.TripId, cancellationToken);
        CreateSeatChangeResult? tripRefusal = RefuseForTrip(items.Find(IsTripItem), now);
        if (tripRefusal != null)
        {
            return tripRefusal;
        }

        Dictionary<string, StoredSeat> seats = ToSeats(items);
        CreateSeatChangeResult? settled = SettleWithoutWriting(change, seats);
        if (settled != null)
        {
            return settled;
        }

        string bookingId = change.BookingId.ToString("D");
        List<SeatMove> pending = change.Moves.FindAll(move => !IsDone(move, seats, bookingId));
        bool isChanged = await TryWriteAsync(change, pending, now, cancellationToken);
        if (!isChanged)
        {
            return null;
        }

        CreateSeatChangeResponse response = ToResponse(change, OwnedAfter(seats, bookingId, pending));
        return new CreateSeatChangeResult { Outcome = CreateSeatChangeOutcome.Changed, Response = response };
    }

    // Null means every seat is known, some moves are still to do, and all of them may go ahead.
    private static CreateSeatChangeResult? SettleWithoutWriting(SeatChangeTarget change, Dictionary<string, StoredSeat> seats)
    {
        List<string> unknown = UnknownSeats(change.Moves, seats);
        if (unknown.Count > 0)
        {
            return new CreateSeatChangeResult { Outcome = CreateSeatChangeOutcome.UnknownSeats, RefusedSeats = unknown };
        }

        string bookingId = change.BookingId.ToString("D");
        List<SeatMove> pending = change.Moves.FindAll(move => !IsDone(move, seats, bookingId));
        if (pending.Count == 0)
        {
            CreateSeatChangeResponse unchanged = ToResponse(change, OwnedAfter(seats, bookingId, pending));
            return new CreateSeatChangeResult { Outcome = CreateSeatChangeOutcome.AlreadyChanged, Response = unchanged };
        }

        return RefuseForMoves(pending, seats, bookingId);
    }

    private static CreateSeatChangeResult? RefuseForTrip(Dictionary<string, AttributeValue>? trip, DateTimeOffset now)
    {
        // A trip known only from an early cancellation has not been announced: not found.
        if (trip is null || !trip.ContainsKey("departureAt"))
        {
            return new CreateSeatChangeResult { Outcome = CreateSeatChangeOutcome.TripNotFound };
        }

        if (trip.ContainsKey("isCancelled") && trip["isCancelled"].BOOL == true)
        {
            return new CreateSeatChangeResult { Outcome = CreateSeatChangeOutcome.TripCancelled };
        }

        DateTimeOffset departureAt = ParseUtc(trip["departureAt"].S);
        if (departureAt <= now)
        {
            return new CreateSeatChangeResult { Outcome = CreateSeatChangeOutcome.TripDeparted, DepartedAt = departureAt };
        }

        return null;
    }

    private static List<string> UnknownSeats(List<SeatMove> moves, Dictionary<string, StoredSeat> seats)
    {
        var unknown = new List<string>();
        foreach (SeatMove move in moves)
        {
            if (!seats.ContainsKey(move.FromSeatNo))
            {
                unknown.Add(move.FromSeatNo);
            }

            if (!seats.ContainsKey(move.ToSeatNo))
            {
                unknown.Add(move.ToSeatNo);
            }
        }

        return unknown;
    }

    // Done by an earlier identical request: the booking has the new seat and no longer the old one.
    // Owning both means the move never happened, so it is decided like any other.
    private static bool IsDone(SeatMove move, Dictionary<string, StoredSeat> seats, string bookingId)
    {
        return IsBookedBy(seats[move.ToSeatNo], bookingId) && !IsBookedBy(seats[move.FromSeatNo], bookingId);
    }

    private static CreateSeatChangeResult? RefuseForMoves(
        List<SeatMove> pending, Dictionary<string, StoredSeat> seats, string bookingId)
    {
        List<SeatMove> notOwned = pending.FindAll(move => !IsBookedBy(seats[move.FromSeatNo], bookingId));
        if (notOwned.Count > 0)
        {
            List<string> seatNos = notOwned.ConvertAll(move => move.FromSeatNo);
            return new CreateSeatChangeResult { Outcome = CreateSeatChangeOutcome.SeatsNotOwned, RefusedSeats = seatNos };
        }

        List<SeatMove> mismatched = pending.FindAll(move => seats[move.FromSeatNo].SeatClass != seats[move.ToSeatNo].SeatClass);
        if (mismatched.Count > 0)
        {
            return new CreateSeatChangeResult { Outcome = CreateSeatChangeOutcome.SeatClassMismatch, RefusedMoves = mismatched };
        }

        List<SeatMove> taken = pending.FindAll(move => seats[move.ToSeatNo].Status != Available);
        if (taken.Count > 0)
        {
            List<string> seatNos = taken.ConvertAll(move => move.ToSeatNo);
            return new CreateSeatChangeResult { Outcome = CreateSeatChangeOutcome.SeatsUnavailable, RefusedSeats = seatNos };
        }

        return null;
    }

    private async Task<bool> TryWriteAsync(
        SeatChangeTarget change, List<SeatMove> pending, DateTimeOffset now, CancellationToken cancellationToken)
    {
        List<TransactWriteItem> writes = CreateSeatChangeWrites.MoveSeats(_tableName, change, pending, now);
        try
        {
            await _dynamoDb.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = writes }, cancellationToken);
        }
        catch (TransactionCanceledException exception) when (CreateSeatChangeWrites.IsLostRace(exception))
        {
            return false;
        }

        return true;
    }

    private static bool IsBookedBy(StoredSeat seat, string bookingId)
    {
        return seat.Status == Booked && seat.BookingId == bookingId;
    }

    // The booking's Booked seats as read, with the moves about to be written applied.
    private static List<string> OwnedAfter(Dictionary<string, StoredSeat> seats, string bookingId, List<SeatMove> applied)
    {
        var owned = new List<string>();
        foreach (KeyValuePair<string, StoredSeat> seat in seats)
        {
            if (IsBookedBy(seat.Value, bookingId))
            {
                owned.Add(seat.Key);
            }
        }

        foreach (SeatMove move in applied)
        {
            owned.Remove(move.FromSeatNo);
            owned.Add(move.ToSeatNo);
        }

        owned.Sort(StringComparer.Ordinal);
        return owned;
    }

    private static Dictionary<string, StoredSeat> ToSeats(List<Dictionary<string, AttributeValue>> items)
    {
        var seats = new Dictionary<string, StoredSeat>(StringComparer.Ordinal);
        foreach (Dictionary<string, AttributeValue> item in items)
        {
            if (IsTripItem(item))
            {
                continue;
            }

            string? bookingId = null;
            if (item.ContainsKey("bookingId"))
            {
                bookingId = item["bookingId"].S;
            }

            seats[item["seatNo"].S] = new StoredSeat(item["status"].S, bookingId, item["seatClass"].S);
        }

        return seats;
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

    private static bool IsTripItem(Dictionary<string, AttributeValue> item)
    {
        return item["sk"].S == TripSortKey;
    }

    private static DateTimeOffset ParseUtc(string text)
    {
        System.Globalization.DateTimeStyles styles = System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal;
        return DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture, styles);
    }

    private static CreateSeatChangeResponse ToResponse(SeatChangeTarget change, List<string> seatNos)
    {
        return new CreateSeatChangeResponse
        {
            TripId = change.TripId,
            BookingId = change.BookingId,
            Changes = change.Moves,
            SeatNos = seatNos,
        };
    }

    /// <summary>What a decision needs to know about one seat.</summary>
    /// <param name="Status">Available, Held, Booked or Blocked.</param>
    /// <param name="BookingId">The booking that holds or owns it, if any.</param>
    /// <param name="SeatClass">The operator's seat class name.</param>
    private sealed record StoredSeat(string Status, string? BookingId, string SeatClass);
}
