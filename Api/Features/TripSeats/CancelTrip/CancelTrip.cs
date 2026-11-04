namespace Api.Features.TripSeats.CancelTrip;

/// <summary>
/// The TripCancelled data Seat Inventory needs, as Fleet's events spell it. Nullable so a missing trip
/// identifier is reported, not thrown.
/// </summary>
public sealed record TripCancelledMessage
{
    public Guid? TripId { get; init; }

    /// <summary>The operator's or Fleet's reason; kept on the log line only.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Waits on the trip-cancelled queue — subscribed to Fleet's events, letting only TripCancelled through —
/// and cancels each trip it names.
/// </summary>
public sealed class CancelTripService : EventQueueService
{
    private readonly CancelTripHandler _handler;

    public CancelTripService(CancelTripHandler handler, IAmazonSQS sqs, IConfiguration configuration, ILogger<CancelTripService> logger)
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
            QueueUrl = configuration[Infrastructure.ConfigKeys.TripCancelledQueueUrl]!,
            DeadLetterQueueUrl = configuration[Infrastructure.ConfigKeys.TripCancelledDeadLetterQueueUrl]!,
            EventTypes = new List<string> { CancelTripHandler.TriggerEventType },
            NotAppliedProblem = ProblemTypes.FleetEventNotApplied,
        };
    }
}
