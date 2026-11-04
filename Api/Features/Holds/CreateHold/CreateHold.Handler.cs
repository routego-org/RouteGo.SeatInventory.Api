namespace Api.Features.Holds.CreateHold;

/// <summary>
/// Holds free seats of a trip for one booking, all or none. The change is one conditional DynamoDB
/// transaction, so of any number of simultaneous holds on a seat exactly one succeeds, and every
/// changed seat records why, by which request and under which correlation identifier — the
/// seat-change stream turns that record into the SeatHeld event.
/// </summary>
public sealed class CreateHoldHandler
{
    private const string TripKeyPrefix = "TRIP#";
    private const string TripSortKey = "TRIP";
    private const string Available = "Available";
    private const string Held = "Held";
    private const string Booked = "Booked";

    // Two holds racing for one seat can both be cancelled as conflicting transactions, with neither
    // winning; a few attempts, each after a fresh read, let one of them through.
    private const int MaxWriteAttempts = 3;

    // STATUS is a DynamoDB reserved word, hence the placeholder.
    private const string Projection = "sk, departureAt, isCancelled, seatNo, #status, bookingId, holdExpiresAt";

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly IAmazonSQS _sqs;
    private readonly IClock _clock;
    private readonly CreateHoldSettings _settings;

    public CreateHoldHandler(IAmazonDynamoDB dynamoDb, IAmazonSQS sqs, IClock clock, CreateHoldSettings settings)
    {
        _dynamoDb = dynamoDb;
        _sqs = sqs;
        _clock = clock;
        _settings = settings;
    }

    public async Task<CreateHoldResult> HandleAsync(
        Guid tripId, CreateHoldRequest request, string correlationId, CancellationToken cancellationToken)
    {
        int holdSeconds = SeatHoldRules.DefaultLengthSeconds;
        if (request.HoldSeconds.HasValue)
        {
            holdSeconds = request.HoldSeconds.Value;
        }

        if (holdSeconds > _settings.MaxHoldSeconds)
        {
            return new CreateHoldResult { Outcome = CreateHoldOutcome.HoldTooLong, MaxHoldSeconds = _settings.MaxHoldSeconds };
        }

        DateTimeOffset now = _clock.UtcNow;
        var hold = new CreateHoldAttempt
        {
            TripId = tripId,
            BookingId = Guid.Parse(request.BookingId!),
            SeatNos = request.SeatNos!,
            ExpiresAt = now.AddSeconds(holdSeconds),
            Now = now,
            CorrelationId = correlationId,
        };

        CreateHoldResult? decision = await DecideAsync(hold, cancellationToken);
        if (decision != null)
        {
            return decision;
        }

        // The timer goes out before any seat changes. If it cannot be sent, nothing is held and
        // Booking simply retries; if the hold then loses a race, the timer finds no seat held by this
        // booking with this expiry and changes nothing.
        await StartTimerAsync(hold, holdSeconds, cancellationToken);
        return await HoldAsync(hold, cancellationToken);
    }

    // Null means every seat is free for this booking and the hold may go ahead.
    private async Task<CreateHoldResult?> DecideAsync(CreateHoldAttempt hold, CancellationToken cancellationToken)
    {
        List<Dictionary<string, AttributeValue>> items = await ListTripItemsAsync(hold.TripId, cancellationToken);
        CreateHoldResult? tripRefusal = RefuseForTrip(items.Find(IsTripItem), hold.Now);
        if (tripRefusal != null)
        {
            return tripRefusal;
        }

        List<Dictionary<string, AttributeValue>> seats = items.FindAll(item => !IsTripItem(item));
        Dictionary<string, string> statusBySeat = ToStatusBySeat(seats);
        List<string> unknown = hold.SeatNos.FindAll(seatNo => !statusBySeat.ContainsKey(seatNo));
        if (unknown.Count > 0)
        {
            return new CreateHoldResult { Outcome = CreateHoldOutcome.UnknownSeats, RefusedSeats = unknown };
        }

        CreateHoldResult? existingHold = DecideForExistingHold(hold, seats);
        if (existingHold != null)
        {
            return existingHold;
        }

        List<string> unavailable = hold.SeatNos.FindAll(seatNo => statusBySeat[seatNo] != Available);
        if (unavailable.Count > 0)
        {
            return new CreateHoldResult { Outcome = CreateHoldOutcome.SeatsUnavailable, RefusedSeats = unavailable };
        }

        return null;
    }

    private static Dictionary<string, string> ToStatusBySeat(List<Dictionary<string, AttributeValue>> seats)
    {
        var statusBySeat = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Dictionary<string, AttributeValue> seat in seats)
        {
            statusBySeat[seat["seatNo"].S] = seat["status"].S;
        }

        return statusBySeat;
    }

    private static CreateHoldResult? RefuseForTrip(Dictionary<string, AttributeValue>? trip, DateTimeOffset now)
    {
        // A trip known only from an early cancellation has not been announced: not found.
        if (trip is null || !trip.ContainsKey("departureAt"))
        {
            return new CreateHoldResult { Outcome = CreateHoldOutcome.TripNotFound };
        }

        if (trip.ContainsKey("isCancelled") && trip["isCancelled"].BOOL == true)
        {
            return new CreateHoldResult { Outcome = CreateHoldOutcome.TripCancelled };
        }

        DateTimeOffset departureAt = ParseUtc(trip["departureAt"].S);
        if (departureAt <= now)
        {
            return new CreateHoldResult { Outcome = CreateHoldOutcome.TripDeparted, DepartedAt = departureAt };
        }

        return null;
    }

    // One hold per booking per trip: exactly the same seats again is a repeat and gets the hold as
    // it stands; any other seats while the booking already has some are refused.
    private static CreateHoldResult? DecideForExistingHold(
        CreateHoldAttempt hold, List<Dictionary<string, AttributeValue>> seats)
    {
        string bookingId = hold.BookingId.ToString("D");
        List<Dictionary<string, AttributeValue>> owned = seats.FindAll(seat => IsHeldOrBookedBy(seat, bookingId));
        if (owned.Count == 0)
        {
            return null;
        }

        List<string> ownedSeatNos = owned.ConvertAll(seat => seat["seatNo"].S);
        HoldStatus status = HoldStatus.Held;
        DateTimeOffset? expiresAt = null;
        if (owned.Exists(seat => seat["status"].S == Booked))
        {
            status = HoldStatus.Booked;
        }
        else
        {
            expiresAt = ParseUtc(owned[0]["holdExpiresAt"].S);
        }

        bool isSameSeats = ownedSeatNos.Count == hold.SeatNos.Count && hold.SeatNos.TrueForAll(ownedSeatNos.Contains);
        if (!isSameSeats)
        {
            return new CreateHoldResult
            {
                Outcome = CreateHoldOutcome.BookingAlreadyHasSeats,
                RefusedSeats = ownedSeatNos,
                ExistingHoldStatus = status,
            };
        }

        CreateHoldResponse response = ToResponse(hold, ownedSeatNos, status, expiresAt);
        return new CreateHoldResult { Outcome = CreateHoldOutcome.AlreadyHeld, Response = response };
    }

    private static bool IsHeldOrBookedBy(Dictionary<string, AttributeValue> seat, string bookingId)
    {
        if (!seat.ContainsKey("bookingId") || seat["bookingId"].S != bookingId)
        {
            return false;
        }

        string status = seat["status"].S;
        return status == Held || status == Booked;
    }

    private async Task StartTimerAsync(CreateHoldAttempt hold, int holdSeconds, CancellationToken cancellationToken)
    {
        var timer = new HoldExpiryTimer
        {
            TripId = hold.TripId,
            BookingId = hold.BookingId,
            HoldExpiresAt = hold.ExpiresAtText,
            SeatNos = hold.SeatNos,
            CorrelationId = hold.CorrelationId,
        };
        var message = new SendMessageRequest
        {
            QueueUrl = _settings.QueueUrl,
            MessageBody = JsonSerializer.Serialize(timer, WireJson.Options),
            DelaySeconds = holdSeconds,
        };
        await _sqs.SendMessageAsync(message, cancellationToken);
    }

    private async Task<CreateHoldResult> HoldAsync(CreateHoldAttempt hold, CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= MaxWriteAttempts; attempt++)
        {
            bool isHeld = await TryHoldAsync(hold, cancellationToken);
            if (isHeld)
            {
                CreateHoldResponse response = ToResponse(hold, hold.SeatNos, HoldStatus.Held, hold.ExpiresAt);
                return new CreateHoldResult { Outcome = CreateHoldOutcome.Held, Response = response };
            }

            // Lost a race since the read: decide again on what the seats are now — taken by another
            // booking, the trip cancelled, or this very hold made by a simultaneous repeat.
            CreateHoldResult? decision = await DecideAsync(hold, cancellationToken);
            if (decision != null)
            {
                return decision;
            }
        }

        return new CreateHoldResult { Outcome = CreateHoldOutcome.SeatsUnavailable, RefusedSeats = hold.SeatNos };
    }

    private async Task<bool> TryHoldAsync(CreateHoldAttempt hold, CancellationToken cancellationToken)
    {
        var transaction = new TransactWriteItemsRequest { TransactItems = CreateHoldWrites.HoldSeats(_settings.TableName, hold) };
        try
        {
            await _dynamoDb.TransactWriteItemsAsync(transaction, cancellationToken);
        }
        catch (TransactionCanceledException exception) when (CreateHoldWrites.IsLostRace(exception))
        {
            return false;
        }

        return true;
    }

    private async Task<List<Dictionary<string, AttributeValue>>> ListTripItemsAsync(
        Guid tripId, CancellationToken cancellationToken)
    {
        // Strongly consistent, so a decision made after a lost race sees the winner's seats.
        var query = new QueryRequest
        {
            TableName = _settings.TableName,
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
        var parsed = DateTimeOffset.Parse(
            text,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal);
        return parsed.ToUniversalTime();
    }

    private static CreateHoldResponse ToResponse(
        CreateHoldAttempt hold, IReadOnlyList<string> seatNos, HoldStatus status, DateTimeOffset? expiresAt)
    {
        return new CreateHoldResponse
        {
            TripId = hold.TripId,
            BookingId = hold.BookingId,
            SeatNos = seatNos,
            Status = status,
            HoldExpiresAt = expiresAt,
        };
    }
}
