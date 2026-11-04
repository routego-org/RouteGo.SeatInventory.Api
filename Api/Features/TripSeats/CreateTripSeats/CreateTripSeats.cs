namespace Api.Features.TripSeats.CreateTripSeats;

/// <summary>
/// The TripScheduled data Seat Inventory needs, as Fleet's events spell it. Fleet's other fields — route,
/// stops, bus, operator name and rating — are Search's and Booking's, and are not read. Every member is
/// nullable so a missing one is reported, not thrown.
/// </summary>
public sealed record TripScheduledMessage
{
    private static readonly string[] _decks = { nameof(Deck.Lower), nameof(Deck.Upper) };

    public Guid? TripId { get; init; }

    /// <summary>The trip's operator: the same value as the operatorId in that operator's access token.</summary>
    public string? OperatorId { get; init; }

    public DateTimeOffset? DepartureUtc { get; init; }

    public List<TripScheduledSeat?>? Seats { get; init; }

    /// <summary>What makes this data unusable, naming the field, or null when every seat can be created from it.</summary>
    public string? DescribeProblem()
    {
        if (TripId is null)
        {
            return "tripId is missing";
        }

        if (!Guid.TryParse(OperatorId, out _))
        {
            return "operatorId is missing or not an identifier";
        }

        if (DepartureUtc is null)
        {
            return "departureUtc is missing";
        }

        if (Seats is null || Seats.Count == 0)
        {
            return "seats is missing or empty";
        }

        return DescribeSeatsProblem(Seats);
    }

    private static string? DescribeSeatsProblem(List<TripScheduledSeat?> seats)
    {
        var seatNos = new HashSet<string>(StringComparer.Ordinal);
        foreach (TripScheduledSeat? seat in seats)
        {
            if (seat is null || !IsComplete(seat))
            {
                return "a seat has no seatNo, deck (Lower or Upper), row, col, class or fare";
            }

            if (!seatNos.Add(seat.SeatNo!))
            {
                return $"two seats have the seatNo {seat.SeatNo}";
            }
        }

        return null;
    }

    private static bool IsComplete(TripScheduledSeat seat)
    {
        bool isPlaced = _decks.Contains(seat.Deck) && seat.Row >= 1 && seat.Col >= 1;
        bool isPriced = !string.IsNullOrWhiteSpace(seat.Class) && seat.Fare >= 0;
        return !string.IsNullOrWhiteSpace(seat.SeatNo) && isPlaced && isPriced;
    }
}

/// <summary>One seat as TripScheduled gives it. Col and Class are Fleet's names for the column and the seat class.</summary>
public sealed record TripScheduledSeat
{
    public string? SeatNo { get; init; }

    /// <summary>Lower or Upper, spelled as Fleet's enum is.</summary>
    public string? Deck { get; init; }

    public int? Row { get; init; }

    public int? Col { get; init; }

    public string? Class { get; init; }

    /// <summary>Whole Indian rupees.</summary>
    public int? Fare { get; init; }
}

/// <summary>
/// Waits on the trip-scheduled queue — subscribed to Fleet's events, letting only TripScheduled through —
/// and creates each announced trip's seats. A TripScheduled that arrived while the API was stopped waits
/// on the queue and is applied once it runs again.
/// </summary>
public sealed class CreateTripSeatsService : EventQueueService
{
    private readonly CreateTripSeatsHandler _handler;

    public CreateTripSeatsService(
        CreateTripSeatsHandler handler, IAmazonSQS sqs, IConfiguration configuration, ILogger<CreateTripSeatsService> logger)
        : base(sqs, QueueOf(configuration), logger)
    {
        _handler = handler;
    }

    protected override async Task<EventOutcome> HandleEventAsync(ReceivedEvent receivedEvent, CancellationToken cancellationToken)
    {
        return await _handler.HandleAsync(receivedEvent, cancellationToken);
    }

    private static EventQueue QueueOf(IConfiguration configuration)
    {
        // Present by the time the worker runs: startup refuses to run without them.
        return new EventQueue
        {
            QueueUrl = configuration[Infrastructure.ConfigKeys.TripScheduledQueueUrl]!,
            DeadLetterQueueUrl = configuration[Infrastructure.ConfigKeys.TripScheduledDeadLetterQueueUrl]!,
            EventTypes = new List<string> { CreateTripSeatsHandler.TriggerEventType },
            NotAppliedProblem = ProblemTypes.FleetEventNotApplied,
        };
    }
}
