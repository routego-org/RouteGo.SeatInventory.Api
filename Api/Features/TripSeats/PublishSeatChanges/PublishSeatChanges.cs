namespace Api.Features.TripSeats.PublishSeatChanges;

/// <summary>What one publishing run did, and the alerts it raised.</summary>
public sealed record PublishSeatChangesResult
{
    /// <summary>True when another copy of the API held the publisher lease, so this run did nothing.</summary>
    public required bool IsSkipped { get; init; }

    /// <summary>Events delivered by this run, audit records included.</summary>
    public int DeliveredCount { get; init; }

    /// <summary>Changes newly found waiting more than five minutes.</summary>
    public IReadOnlyList<DelayedSeatChange> Delayed { get; init; } = Array.Empty<DelayedSeatChange>();

    /// <summary>Changes that can never become an event, passed over.</summary>
    public IReadOnlyList<UnreadableSeatChange> Unreadable { get; init; } = Array.Empty<UnreadableSeatChange>();

    /// <summary>Changes that could not be delivered this run, each holding back the rest of its shard.</summary>
    public IReadOnlyList<HeldBackSeatChange> HeldBack { get; init; } = Array.Empty<HeldBackSeatChange>();
}

/// <summary>A change still not delivered more than five minutes after it was made.</summary>
public sealed record DelayedSeatChange
{
    public required string ShardId { get; init; }

    /// <summary>The request whose change is the oldest not yet delivered.</summary>
    public required string Request { get; init; }

    public required DateTimeOffset OldestChangeAt { get; init; }

    /// <summary>How far behind the publisher is, in seconds.</summary>
    public required long LagSeconds { get; init; }
}

/// <summary>A recorded change that can never become an event.</summary>
/// <param name="ShardId">The shard it is on.</param>
/// <param name="Request">Its request, or its record position when it names none.</param>
/// <param name="Problem">What is wrong with it.</param>
public sealed record UnreadableSeatChange(string ShardId, string Request, string Problem);

/// <summary>A change this run could not deliver, and why.</summary>
/// <param name="ShardId">The shard it holds back.</param>
/// <param name="Request">Its request.</param>
/// <param name="Failure">What the messaging service did.</param>
public sealed record HeldBackSeatChange(string ShardId, string Request, Exception Failure);

/// <summary>
/// Runs the publisher every five seconds for as long as the API process runs. A failed run is logged
/// and the next one retries, so an outage of the stream, the table or the messaging service never
/// stops the loop.
/// </summary>
public sealed class PublishSeatChangesService : BackgroundService
{
    private static readonly TimeSpan _interval = TimeSpan.FromSeconds(5);

    private readonly PublishSeatChangesHandler _handler;
    private readonly ILogger<PublishSeatChangesService> _logger;

    public PublishSeatChangesService(PublishSeatChangesHandler handler, ILogger<PublishSeatChangesService> logger)
    {
        _handler = handler;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunOnceAsync(stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            PublishSeatChangesResult result = await _handler.HandleAsync(stoppingToken);
            Report(result);
        }
        // A timeout inside the run is a failed run, not a shutdown; letting it escape would stop the host.
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogError(exception, "Seat event publishing run failed; the next run retries");
        }
    }

    private void Report(PublishSeatChangesResult result)
    {
        if (result.DeliveredCount > 0)
        {
            _logger.LogInformation("Delivered {DeliveredCount} seat events", result.DeliveredCount);
        }

        foreach (HeldBackSeatChange heldBack in result.HeldBack)
        {
            _logger.LogWarning(
                heldBack.Failure, "Seat change {Request} on shard {ShardId} could not be delivered; later changes wait behind it",
                heldBack.Request, heldBack.ShardId);
        }

        foreach (DelayedSeatChange delayed in result.Delayed)
        {
            _logger.LogError(
                "{Code}: seat change {Request} recorded at {OldestChangeAt} is still unpublished, {LagSeconds} seconds behind",
                PublishSeatChangesHandler.SeatEventDelayedCode, delayed.Request, delayed.OldestChangeAt, delayed.LagSeconds);
        }

        foreach (UnreadableSeatChange unreadable in result.Unreadable)
        {
            _logger.LogError(
                "{Code}: seat change {Request} on shard {ShardId} cannot become an event and is parked: {Problem}",
                PublishSeatChangesHandler.SeatEventDelayedCode, unreadable.Request, unreadable.ShardId, unreadable.Problem);
        }
    }
}
