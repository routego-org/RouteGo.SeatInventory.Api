namespace Api.Common.Messaging;

/// <summary>
/// Waits on one event queue for as long as the API process runs, and hands each event to its worker
/// one at a time. What happens to the message follows the worker's outcome: an applied event is
/// deleted; an event that can never be applied is set aside on the dead-letter queue at once; an
/// event that cannot be applied yet, or whose handling failed, stays — the queue delivers it again a
/// little later, and after the last allowed delivery parks it on the dead-letter queue itself. Every
/// event not applied for good raises the queue's alert, naming the event and the reason.
/// </summary>
public abstract class EventQueueService : BackgroundService
{
    // The queues' redrive policy parks a message after this many deliveries (scripts/local-setup.sh and
    // the deployment set it); the two must say the same number.
    private const int MaxDeliveries = 5;

    // Long polling: one request waits up to this long for an event, so an idle queue costs a few calls a minute.
    private const int WaitSeconds = 20;
    private const int MaxMessagesPerReceive = 10;
    private const string ReceiveCountAttribute = "ApproximateReceiveCount";

    private static readonly TimeSpan _pauseAfterFailure = TimeSpan.FromSeconds(5);

    private readonly IAmazonSQS _sqs;
    private readonly EventQueue _queue;
    private readonly string _eventTypes;
    private readonly ILogger _logger;

    protected EventQueueService(IAmazonSQS sqs, EventQueue queue, ILogger logger)
    {
        _sqs = sqs;
        _queue = queue;
        _eventTypes = string.Join(" or ", queue.EventTypes);
        _logger = logger;
    }

    /// <summary>Waits up to 20 seconds for events and handles each one; returns how many were applied.</summary>
    public async Task<int> HandleWaitingEventsAsync(CancellationToken cancellationToken)
    {
        var request = new ReceiveMessageRequest
        {
            QueueUrl = _queue.QueueUrl,
            MaxNumberOfMessages = MaxMessagesPerReceive,
            WaitTimeSeconds = WaitSeconds,
            MessageSystemAttributeNames = new List<string> { ReceiveCountAttribute },
        };
        ReceiveMessageResponse response = await _sqs.ReceiveMessageAsync(request, cancellationToken);
        if (response.Messages is null)
        {
            return 0;
        }

        int appliedCount = 0;
        foreach (Message message in response.Messages)
        {
            bool isApplied = await HandleMessageAsync(message, cancellationToken);
            if (isApplied)
            {
                appliedCount++;
            }
        }

        return appliedCount;
    }

    /// <summary>Applies one event of a type this queue carries and says what should happen to its message.</summary>
    protected abstract Task<EventOutcome> HandleEventAsync(ReceivedEvent receivedEvent, CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            bool isReceiving = await TryHandleWaitingEventsAsync(stoppingToken);
            if (!isReceiving)
            {
                await Task.Delay(_pauseAfterFailure, stoppingToken);
            }
        }
    }

    private async Task<bool> TryHandleWaitingEventsAsync(CancellationToken stoppingToken)
    {
        try
        {
            await HandleWaitingEventsAsync(stoppingToken);
            return true;
        }
        // The queue itself unreachable: the events wait on it, safe, until the next attempt.
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogError(exception, "Receiving {EventType} events failed; trying again shortly", _eventTypes);
            return false;
        }
    }

    // One event at a time, each failing on its own: one bad event never holds up the others.
    private async Task<bool> HandleMessageAsync(Message message, CancellationToken cancellationToken)
    {
        EventEnvelopeReading reading = EventEnvelopeReader.Read(message.Body);
        if (reading.Event is null)
        {
            _logger.LogError(
                "{Code}: queue message {MessageId} is not a usable {EventType} event ({Problem}), reason {Reason}; set aside on the dead-letter queue",
                _queue.NotAppliedProblem.Code, message.MessageId, _eventTypes, reading.Problem, EventOutcome.InvalidEventData);
            await SetAsideAsync(message, cancellationToken);
            return false;
        }

        EventOutcome? outcome = await TryHandleEventAsync(message, reading.Event, cancellationToken);
        if (outcome is null)
        {
            return false;
        }

        return await SettleAsync(message, reading.Event, outcome, cancellationToken);
    }

    // Null when handling failed; the failure is already reported and the message stays for the next delivery.
    private async Task<EventOutcome?> TryHandleEventAsync(Message message, ReceivedEvent receivedEvent, CancellationToken cancellationToken)
    {
        if (!_queue.EventTypes.Contains(receivedEvent.EventType))
        {
            return EventOutcome.Rejected($"the event type is {receivedEvent.EventType}, but this queue carries only {_eventTypes}");
        }

        try
        {
            return await HandleEventAsync(receivedEvent, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            ReportFailure(message, receivedEvent, exception);
            return null;
        }
    }

    private async Task<bool> SettleAsync(Message message, ReceivedEvent receivedEvent, EventOutcome outcome, CancellationToken cancellationToken)
    {
        switch (outcome.Kind)
        {
            case EventOutcomeKind.Applied:
                await _sqs.DeleteMessageAsync(_queue.QueueUrl, message.ReceiptHandle, cancellationToken);
                return true;
            case EventOutcomeKind.Rejected:
                LogNotApplied(receivedEvent, outcome.Reason!, outcome.Problem!, null);
                await SetAsideAsync(message, cancellationToken);
                return false;
            default:
                ReportPostponed(message, receivedEvent, outcome);
                return false;
        }
    }

    private void ReportPostponed(Message message, ReceivedEvent receivedEvent, EventOutcome outcome)
    {
        int delivery = DeliveryNumber(message);
        if (delivery < MaxDeliveries)
        {
            _logger.LogWarning(
                "{EventType} event {EventId} (correlation {CorrelationId}) cannot be applied yet ({Problem}); delivery {Delivery} of {MaxDeliveries}, the queue delivers it again",
                receivedEvent.EventType, receivedEvent.EventId, receivedEvent.CorrelationId, outcome.Problem, delivery, MaxDeliveries);
            return;
        }

        LogNotApplied(receivedEvent, outcome.Reason!, outcome.Problem!, null);
    }

    private void ReportFailure(Message message, ReceivedEvent receivedEvent, Exception exception)
    {
        int delivery = DeliveryNumber(message);
        if (delivery < MaxDeliveries)
        {
            _logger.LogWarning(
                exception, "{EventType} event {EventId} (correlation {CorrelationId}) failed on delivery {Delivery} of {MaxDeliveries}; the queue delivers it again",
                receivedEvent.EventType, receivedEvent.EventId, receivedEvent.CorrelationId, delivery, MaxDeliveries);
            return;
        }

        LogNotApplied(receivedEvent, EventOutcome.RepeatedFailure, exception.Message, exception);
    }

    // The alert line a metric filter matches on; the queue's redrive policy parks a postponed or failing
    // event right after it, and a rejected one has just been set aside.
    private void LogNotApplied(ReceivedEvent receivedEvent, string reason, string problem, Exception? exception)
    {
        _logger.LogError(
            exception, "{Code}: {EventType} event {EventId} (correlation {CorrelationId}) was not applied, reason {Reason}: {Problem}",
            _queue.NotAppliedProblem.Code, receivedEvent.EventType, receivedEvent.EventId, receivedEvent.CorrelationId, reason, problem);
    }

    // The message goes to the dead-letter queue unchanged, so the platform engineer can replay it onto
    // this queue after the fix.
    private async Task SetAsideAsync(Message message, CancellationToken cancellationToken)
    {
        await _sqs.SendMessageAsync(_queue.DeadLetterQueueUrl, message.Body, cancellationToken);
        await _sqs.DeleteMessageAsync(_queue.QueueUrl, message.ReceiptHandle, cancellationToken);
    }

    private static int DeliveryNumber(Message message)
    {
        if (message.Attributes is null || !message.Attributes.ContainsKey(ReceiveCountAttribute))
        {
            return 1;
        }

        return int.Parse(message.Attributes[ReceiveCountAttribute], System.Globalization.CultureInfo.InvariantCulture);
    }
}
