namespace Api.Features.TripSeats.RepublishSeats;

/// <summary>
/// Re-sends the current state of every taken seat of the chosen trips as the seat events that already
/// exist, so a subscriber that lost its copy can catch up. Read only: no seat changes, nothing is written
/// to the seat-change stream, and Booking is never affected. A trip that has departed, is cancelled or
/// is not known is skipped and reported; a trip whose events cannot all be sent is retried, then reported
/// as failed while the run carries on with the others.
/// </summary>
public sealed class RepublishSeatsHandler
{
    private const string TripKeyPrefix = "TRIP#";
    private const string TripSortKey = "TRIP";
    private const int MaxSendAttempts = 3;

    // STATUS is a DynamoDB reserved word, hence the placeholder.
    private const string Projection =
        "sk, operatorId, departureAt, isCancelled, seatNo, #status, bookingId, holdExpiresAt, blockReason, lastChangedAt";

    private static readonly TimeSpan _retryDelay = TimeSpan.FromSeconds(1);

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;
    private readonly SeatEventSender _sender;
    private readonly IClock _clock;

    public RepublishSeatsHandler(IAmazonDynamoDB dynamoDb, IConfiguration configuration, SeatEventSender sender, IClock clock)
    {
        _dynamoDb = dynamoDb;
        // Present by the time a run starts: startup refuses to run without it.
        _tableName = configuration[Infrastructure.ConfigKeys.SeatStoreTableName]!;
        _sender = sender;
        _clock = clock;
    }

    /// <summary>Runs once; safe to run again at any time, including while seats are being sold.</summary>
    /// <param name="request">Which trips, and optionally the run's correlation identifier.</param>
    /// <param name="cancellationToken">Stops the run.</param>
    /// <returns>One line per trip: re-published, skipped or failed.</returns>
    public async Task<RepublishSeatsResult> HandleAsync(RepublishSeatsRequest request, CancellationToken cancellationToken)
    {
        string correlationId = request.CorrelationId ?? Guid.NewGuid().ToString("D");
        List<Guid> tripIds = await ChooseTripsAsync(request, cancellationToken);
        var reports = new List<RepublishTripReport>();
        foreach (Guid tripId in tripIds)
        {
            RepublishTripReport report = await RepublishTripAsync(tripId, correlationId, cancellationToken);
            reports.Add(report);
        }

        return new RepublishSeatsResult { CorrelationId = correlationId, Trips = reports };
    }

    private async Task<List<Guid>> ChooseTripsAsync(RepublishSeatsRequest request, CancellationToken cancellationToken)
    {
        if (request.IsAllUpcoming)
        {
            return await ListUpcomingTripsAsync(cancellationToken);
        }

        return request.TripIds.Distinct().ToList();
    }

    private async Task<RepublishTripReport> RepublishTripAsync(Guid tripId, string correlationId, CancellationToken cancellationToken)
    {
        try
        {
            List<Dictionary<string, AttributeValue>> items = await ListTripItemsAsync(tripId, cancellationToken);
            Dictionary<string, AttributeValue>? trip = items.Find(IsTripItem);
            RepublishTripOutcome? skipped = FindSkipReason(trip);
            if (skipped != null)
            {
                return new RepublishTripReport { TripId = tripId, Outcome = skipped.Value };
            }

            List<Dictionary<string, AttributeValue>> seats = items.FindAll(item => !IsTripItem(item));
            List<SeatEventMessage> events = RepublishedSeatEvents.Plan(tripId, trip!["operatorId"].S, seats, correlationId);
            foreach (SeatEventMessage seatEvent in events)
            {
                await SendWithRetryAsync(seatEvent, cancellationToken);
            }

            return new RepublishTripReport { TripId = tripId, Outcome = RepublishTripOutcome.Republished, EventCount = events.Count };
        }
        // One trip's trouble is that trip's report; the run goes on with the others and ends as failed.
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new RepublishTripReport { TripId = tripId, Outcome = RepublishTripOutcome.Failed, Failure = exception };
        }
    }

    // A trip known only from an early TripCancelled has no departure time and no seats: it is not known yet.
    private RepublishTripOutcome? FindSkipReason(Dictionary<string, AttributeValue>? trip)
    {
        if (trip is null || !trip.ContainsKey("departureAt"))
        {
            return RepublishTripOutcome.NotKnown;
        }

        if (IsCancelled(trip))
        {
            return RepublishTripOutcome.Cancelled;
        }

        if (HasDeparted(trip))
        {
            return RepublishTripOutcome.Departed;
        }

        return null;
    }

    private async Task SendWithRetryAsync(SeatEventMessage seatEvent, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await _sender.SendAsync(seatEvent, cancellationToken);
                return;
            }
            // The event keeps its identity across attempts, so a send that did arrive is harmless to repeat.
            catch (Exception) when (attempt < MaxSendAttempts && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(_retryDelay * attempt, cancellationToken);
            }
        }
    }

    // A scan, because the table has no index on departure: the job is run by hand, and it reads only
    // the trip records' own attributes. A trip that changes between this scan and its own read is
    // judged again there.
    private async Task<List<Guid>> ListUpcomingTripsAsync(CancellationToken cancellationToken)
    {
        var scan = new ScanRequest
        {
            TableName = _tableName,
            FilterExpression = "sk = :trip",
            ProjectionExpression = "pk, departureAt, isCancelled",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { { ":trip", new AttributeValue { S = TripSortKey } } },
        };

        var tripIds = new List<Guid>();
        do
        {
            ScanResponse page = await _dynamoDb.ScanAsync(scan, cancellationToken);
            foreach (Dictionary<string, AttributeValue> trip in page.Items)
            {
                if (trip.ContainsKey("departureAt") && !IsCancelled(trip) && !HasDeparted(trip))
                {
                    tripIds.Add(Guid.Parse(trip["pk"].S.Substring(TripKeyPrefix.Length)));
                }
            }

            scan.ExclusiveStartKey = page.LastEvaluatedKey;
        }
        while (scan.ExclusiveStartKey != null && scan.ExclusiveStartKey.Count > 0);

        return tripIds;
    }

    private async Task<List<Dictionary<string, AttributeValue>>> ListTripItemsAsync(Guid tripId, CancellationToken cancellationToken)
    {
        // Strongly consistent so a hold made a moment ago is seen; a read takes no lock on the seats.
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
            items.AddRange(page.Items);
            query.ExclusiveStartKey = page.LastEvaluatedKey;
        }
        while (query.ExclusiveStartKey != null && query.ExclusiveStartKey.Count > 0);

        return items;
    }

    private bool HasDeparted(Dictionary<string, AttributeValue> trip)
    {
        var departureAt = DateTimeOffset.Parse(
            trip["departureAt"].S, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal);
        return departureAt <= _clock.UtcNow;
    }

    private static bool IsCancelled(Dictionary<string, AttributeValue> trip)
    {
        return trip.ContainsKey("isCancelled") && trip["isCancelled"].BOOL == true;
    }

    private static bool IsTripItem(Dictionary<string, AttributeValue> item)
    {
        return item["sk"].S == TripSortKey;
    }
}
