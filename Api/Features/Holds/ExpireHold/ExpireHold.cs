namespace Api.Features.Holds.ExpireHold;

/// <summary>
/// A hold's timer, as CreateHold put it on the hold-expiry queue; the queue delivers it at the hold
/// expiry. The worker keeps its own copy of the shape rather than sharing CreateHold's.
/// </summary>
public sealed record HoldExpiryTimerMessage
{
    public required Guid TripId { get; init; }

    public required Guid BookingId { get; init; }

    /// <summary>The hold expiry exactly as stored on the seats, which is what makes it a key.</summary>
    public required string HoldExpiresAt { get; init; }

    /// <summary>The seats held when the timer started; for information only — the seats are read again.</summary>
    public required List<string> SeatNos { get; init; }

    /// <summary>The hold request's correlation identifier, carried on to the release.</summary>
    public required string CorrelationId { get; init; }
}

/// <summary>What a fired timer freed.</summary>
/// <param name="ReleasedSeatNos">The seats freed; empty when the hold was already gone.</param>
public sealed record ExpireHoldResult(IReadOnlyList<string> ReleasedSeatNos);

/// <summary>
/// Waits on the hold-expiry queue for as long as the API process runs and hands each due timer to the
/// handler. A timer is removed from the queue only once handled; one that fails stays, and the queue
/// delivers it again a little later — after the last allowed delivery the queue parks it, and this
/// worker raises the alert.
/// </summary>
public sealed class ExpireHoldService : BackgroundService
{
    /// <summary>The alert code a CloudWatch metric filter matches on.</summary>
    public const string HoldExpiryFailedCode = "HOLD_EXPIRY_FAILED";

    // The queue's redrive policy parks a timer after this many deliveries (scripts/local-setup.sh and
    // the deployment set it); the two must say the same number.
    private const int MaxDeliveries = 5;

    // Long polling: one request waits up to this long for a timer, so an idle queue costs a few calls a minute.
    private const int WaitSeconds = 20;
    private const int MaxMessagesPerReceive = 10;
    private const string ReceiveCountAttribute = "ApproximateReceiveCount";

    private static readonly TimeSpan _pauseAfterFailure = TimeSpan.FromSeconds(5);

    private readonly ExpireHoldHandler _handler;
    private readonly IAmazonSQS _sqs;
    private readonly string _queueUrl;
    private readonly ILogger<ExpireHoldService> _logger;

    public ExpireHoldService(ExpireHoldHandler handler, IAmazonSQS sqs, IConfiguration configuration, ILogger<ExpireHoldService> logger)
    {
        _handler = handler;
        _sqs = sqs;
        // Present by the time the worker runs: startup refuses to run without it.
        _queueUrl = configuration[Infrastructure.ConfigKeys.HoldExpiryQueueUrl]!;
        _logger = logger;
    }

    /// <summary>Waits up to 20 seconds for due timers and handles each one; returns how many were handled.</summary>
    public async Task<int> ExpireDueHoldsAsync(CancellationToken cancellationToken)
    {
        var request = new ReceiveMessageRequest
        {
            QueueUrl = _queueUrl,
            MaxNumberOfMessages = MaxMessagesPerReceive,
            WaitTimeSeconds = WaitSeconds,
            MessageSystemAttributeNames = new List<string> { ReceiveCountAttribute },
        };
        ReceiveMessageResponse response = await _sqs.ReceiveMessageAsync(request, cancellationToken);
        if (response.Messages is null)
        {
            return 0;
        }

        int handledCount = 0;
        foreach (Message message in response.Messages)
        {
            bool isHandled = await TryHandleAsync(message, cancellationToken);
            if (isHandled)
            {
                handledCount++;
            }
        }

        return handledCount;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            bool isReceiving = await TryExpireDueHoldsAsync(stoppingToken);
            if (!isReceiving)
            {
                await Task.Delay(_pauseAfterFailure, stoppingToken);
            }
        }
    }

    private async Task<bool> TryExpireDueHoldsAsync(CancellationToken stoppingToken)
    {
        try
        {
            await ExpireDueHoldsAsync(stoppingToken);
            return true;
        }
        // The queue itself unreachable: the timers wait on it, safe, until the next attempt.
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogError(exception, "Receiving hold timers failed; trying again shortly");
            return false;
        }
    }

    // One timer at a time, each failing on its own: one bad timer never holds up the others.
    private async Task<bool> TryHandleAsync(Message message, CancellationToken cancellationToken)
    {
        HoldExpiryTimerMessage? timer = ReadTimer(message);
        if (timer is null)
        {
            _logger.LogError(
                "Hold timer message {MessageId} cannot be read; it stays on the queue, which parks it after {MaxDeliveries} deliveries",
                message.MessageId, MaxDeliveries);
            return false;
        }

        try
        {
            ExpireHoldResult result = await _handler.HandleAsync(timer, cancellationToken);
            await _sqs.DeleteMessageAsync(_queueUrl, message.ReceiptHandle, cancellationToken);
            _logger.LogInformation(
                "Hold timer for booking {BookingId} on trip {TripId} freed {SeatCount} seats",
                timer.BookingId, timer.TripId, result.ReleasedSeatNos.Count);
            return true;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            ReportFailure(message, timer, exception);
            return false;
        }
    }

    private void ReportFailure(Message message, HoldExpiryTimerMessage timer, Exception exception)
    {
        int delivery = DeliveryNumber(message);
        if (delivery < MaxDeliveries)
        {
            _logger.LogWarning(
                exception, "Hold timer for booking {BookingId} on trip {TripId} failed on delivery {Delivery} of {MaxDeliveries}; the queue delivers it again",
                timer.BookingId, timer.TripId, delivery, MaxDeliveries);
            return;
        }

        _logger.LogError(
            exception, "{Code}: hold timer for trip {TripId}, booking {BookingId}, hold expiry {HoldExpiresAt} failed through every retry and is parked; the seats stay Held",
            HoldExpiryFailedCode, timer.TripId, timer.BookingId, timer.HoldExpiresAt);
    }

    private static int DeliveryNumber(Message message)
    {
        if (message.Attributes is null || !message.Attributes.ContainsKey(ReceiveCountAttribute))
        {
            return 1;
        }

        return int.Parse(message.Attributes[ReceiveCountAttribute], System.Globalization.CultureInfo.InvariantCulture);
    }

    // A body that is not a timer gets null, and is reported rather than guessed at.
    private static HoldExpiryTimerMessage? ReadTimer(Message message)
    {
        try
        {
            return JsonSerializer.Deserialize<HoldExpiryTimerMessage>(message.Body, WireJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
