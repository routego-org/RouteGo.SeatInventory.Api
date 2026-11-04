namespace Api.Features.TripSeats.UpdateTripFares;

/// <summary>
/// The FareChanged data Seat Inventory needs, as Fleet's events spell it: the trip and every seat with
/// its new fare. Fleet's per-class fares are not read — every seat carries its own. Every member is
/// nullable so a missing one is reported, not thrown.
/// </summary>
public sealed record FareChangedMessage
{
    public Guid? TripId { get; init; }

    public List<FareChangedSeat?>? Seats { get; init; }

    /// <summary>What makes this data unusable, naming the field, or null when every fare can be applied from it.</summary>
    public string? DescribeProblem()
    {
        if (TripId is null)
        {
            return "tripId is missing";
        }

        if (Seats is null || Seats.Count == 0)
        {
            return "seats is missing or empty";
        }

        var seatNos = new HashSet<string>(StringComparer.Ordinal);
        foreach (FareChangedSeat? seat in Seats)
        {
            if (seat is null || string.IsNullOrWhiteSpace(seat.SeatNo) || seat.Fare is null || seat.Fare < 0)
            {
                return "a seat has no seatNo or fare";
            }

            if (!seatNos.Add(seat.SeatNo))
            {
                return $"two seats have the seatNo {seat.SeatNo}";
            }
        }

        return null;
    }
}

/// <summary>One seat's new fare as FareChanged gives it.</summary>
/// <param name="SeatNo">The seat label.</param>
/// <param name="Fare">The new fare, whole Indian rupees.</param>
public sealed record FareChangedSeat(string? SeatNo, int? Fare);

/// <summary>
/// Waits on the fare-changed queue — subscribed to Fleet's events, letting only FareChanged through —
/// and replaces each re-priced trip's seat fares.
/// </summary>
public sealed class UpdateTripFaresService : EventQueueService
{
    private readonly UpdateTripFaresHandler _handler;

    public UpdateTripFaresService(
        UpdateTripFaresHandler handler, IAmazonSQS sqs, IConfiguration configuration, ILogger<UpdateTripFaresService> logger)
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
            QueueUrl = configuration[Infrastructure.ConfigKeys.FareChangedQueueUrl]!,
            DeadLetterQueueUrl = configuration[Infrastructure.ConfigKeys.FareChangedDeadLetterQueueUrl]!,
            EventTypes = new List<string> { UpdateTripFaresHandler.TriggerEventType },
            NotAppliedProblem = ProblemTypes.FleetEventNotApplied,
        };
    }
}
