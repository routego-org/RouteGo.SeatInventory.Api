namespace Api.Infrastructure.Messaging;

/// <summary>
/// Waits on the booking queue — subscribed to Booking's events, letting only BookingFailed and
/// BookingCancelled through — and frees the seats each one names. One queue serves both slices, so this
/// reader lives outside them and hands each event to its slice through the dispatcher.
/// </summary>
public sealed class BookingEventsService : EventQueueService
{
    private readonly BookingEventDispatcher _dispatcher;

    public BookingEventsService(
        BookingEventDispatcher dispatcher, IAmazonSQS sqs, IConfiguration configuration, ILogger<BookingEventsService> logger)
        : base(sqs, QueueOf(configuration), logger)
    {
        _dispatcher = dispatcher;
    }

    protected override async Task<EventOutcome> HandleEventAsync(ReceivedEvent receivedEvent, CancellationToken cancellationToken)
    {
        return await _dispatcher.DispatchAsync(receivedEvent, cancellationToken);
    }

    private static EventQueue QueueOf(IConfiguration configuration)
    {
        // Present by the time the worker runs: startup refuses to run without them.
        return new EventQueue
        {
            QueueUrl = configuration[ConfigKeys.BookingEventsQueueUrl]!,
            DeadLetterQueueUrl = configuration[ConfigKeys.BookingEventsDeadLetterQueueUrl]!,
            EventTypes = BookingEventDispatcher.EventTypes,
            NotAppliedProblem = ProblemTypes.BookingEventNotApplied,
        };
    }
}
