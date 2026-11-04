namespace Api.Features.TripSeats.PublishSeatChanges;

/// <summary>
/// One publishing run: while this copy of the API holds the publisher lease, reads every shard of the
/// seat-change stream from where it last stopped, delivers one event per request (and an audit record
/// per staff action), and records how far it got. Delivery is at least once and repeat-safe: progress
/// is saved only after delivery, and the event identifier comes from the request, so a change read
/// again after a crash or an outage is published again with the same identifier. The first event that
/// cannot be delivered holds back every later change on its shard, so one seat's events never overtake
/// each other.
/// </summary>
public sealed class PublishSeatChangesHandler
{
    /// <summary>The alert code a CloudWatch metric filter matches on.</summary>
    public const string SeatEventDelayedCode = "SEAT_EVENT_DELAYED";

    private static readonly TimeSpan _delayAlertAfter = TimeSpan.FromMinutes(5);

    // Saving progress is itself a change on the stream, so progress made only past the publisher's own
    // items (its lease, its progress) is saved now and then, not on every run — otherwise every save
    // would give the next run one more record to read and save past.
    private static readonly TimeSpan _quietSaveInterval = TimeSpan.FromMinutes(1);

    private readonly SeatPublisherProgress _progress;
    private readonly SeatChangeStream _stream;
    private readonly SeatEventSender _sender;
    private readonly IClock _clock;

    public PublishSeatChangesHandler(
        SeatPublisherProgress progress, SeatChangeStream stream, SeatEventSender sender, IClock clock)
    {
        _progress = progress;
        _stream = stream;
        _sender = sender;
        _clock = clock;
    }

    /// <summary>Runs once; safe to run again at any time, on any copy of the API.</summary>
    /// <param name="cancellationToken">Stops the run; whatever was not delivered waits for the next one.</param>
    /// <returns>What the run did, including the alerts it raised.</returns>
    public async Task<PublishSeatChangesResult> HandleAsync(CancellationToken cancellationToken)
    {
        bool isLeaseHeld = await _progress.TryTakeLeaseAsync(cancellationToken);
        if (!isLeaseHeld)
        {
            return new PublishSeatChangesResult { IsSkipped = true };
        }

        List<ChangeShard> shards = await _stream.ListShardsAsync(cancellationToken);
        Dictionary<string, ShardProgress> saved = await _progress.ListShardsAsync(cancellationToken);
        var deliveries = new List<ShardDelivery>();
        foreach (ChangeShard shard in shards)
        {
            if (IsReady(shard, shards, saved))
            {
                ShardProgress? shardProgress = saved.GetValueOrDefault(shard.ShardId);
                ShardDelivery delivery = await PublishShardAsync(shard, shardProgress, cancellationToken);
                deliveries.Add(delivery);
            }
        }

        return ToResult(deliveries);
    }

    // A shard that split from another waits until its parent is finished, so a seat's changes on the
    // parent are published before its changes on the child.
    private static bool IsReady(ChangeShard shard, List<ChangeShard> shards, Dictionary<string, ShardProgress> saved)
    {
        if (saved.TryGetValue(shard.ShardId, out ShardProgress? own) && own.IsFinished)
        {
            return false;
        }

        // A parent the stream no longer holds was trimmed away after 24 hours; there is nothing to wait for.
        bool isParentHeld = shard.ParentShardId != null && shards.Exists(other => other.ShardId == shard.ParentShardId);
        if (!isParentHeld)
        {
            return true;
        }

        return saved.TryGetValue(shard.ParentShardId!, out ShardProgress? parent) && parent.IsFinished;
    }

    private async Task<ShardDelivery> PublishShardAsync(ChangeShard shard, ShardProgress? saved, CancellationToken cancellationToken)
    {
        string? afterSequenceNumber = saved?.SequenceNumber;
        ShardRead read = await _stream.ReadAsync(shard, afterSequenceNumber, cancellationToken);
        List<SeatChangeGroup> groups = SeatEvents.Group(read.Entries);
        List<string> tripIds = SeatEvents.ListTripsNeedingOperator(groups);
        Dictionary<string, string> operatorByTrip = await _stream.ListTripOperatorsAsync(tripIds, cancellationToken);

        var delivery = new ShardDelivery(shard.ShardId);
        await DeliverAsync(groups, operatorByTrip, delivery, cancellationToken);
        delivery.Delayed = FindDelay(saved, delivery);

        ShardProgress? next = NextProgress(saved, read, delivery);
        if (next != null)
        {
            await _progress.SaveShardAsync(next, cancellationToken);
        }

        return delivery;
    }

    private async Task DeliverAsync(
        List<SeatChangeGroup> groups, Dictionary<string, string> operatorByTrip, ShardDelivery delivery, CancellationToken cancellationToken)
    {
        foreach (SeatChangeGroup group in groups)
        {
            SeatEventPlan plan = SeatEvents.Plan(group, operatorByTrip);
            // A change that can never become an event is reported for the platform engineer and passed
            // over: holding everything behind it would stop the publisher for good.
            if (plan.Problem != null)
            {
                delivery.Unreadable.Add(new UnreadableSeatChange(delivery.ShardId, group.Key, plan.Problem));
                delivery.Handled.Add(group.Key);
                continue;
            }

            bool isSent = await TrySendAllAsync(plan.Messages, delivery, cancellationToken);
            if (!isSent)
            {
                delivery.Stuck = group;
                return;
            }

            delivery.Handled.Add(group.Key);
        }
    }

    private async Task<bool> TrySendAllAsync(List<SeatEventMessage> messages, ShardDelivery delivery, CancellationToken cancellationToken)
    {
        foreach (SeatEventMessage message in messages)
        {
            try
            {
                await _sender.SendAsync(message, cancellationToken);
            }
            // Only our own shutdown ends the run. Any other failure — the messaging service down, a
            // timeout — holds this change and every later one back until the next run.
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                delivery.Failure = exception;
                return false;
            }

            delivery.DeliveredCount++;
        }

        return true;
    }

    // Raised once per held-back change, after it has waited more than five minutes.
    private DelayedSeatChange? FindDelay(ShardProgress? saved, ShardDelivery delivery)
    {
        if (delivery.Stuck is null)
        {
            return null;
        }

        TimeSpan lag = _clock.UtcNow - delivery.Stuck.FirstRecordedAt;
        bool isAlreadyAlerted = saved != null && saved.AlertedRequest == delivery.Stuck.Key;
        if (lag <= _delayAlertAfter || isAlreadyAlerted)
        {
            return null;
        }

        return new DelayedSeatChange
        {
            ShardId = delivery.ShardId,
            Request = delivery.Stuck.Key,
            OldestChangeAt = delivery.Stuck.FirstRecordedAt,
            LagSeconds = (long)lag.TotalSeconds,
        };
    }

    // Null when there is nothing worth saving this run.
    private ShardProgress? NextProgress(ShardProgress? saved, ShardRead read, ShardDelivery delivery)
    {
        HandledRange handled = HandledUpTo(read.Entries, delivery.Handled);
        bool isFinished = read.IsClosed && delivery.Stuck is null;
        bool isQuietSaveDue = saved is null || _clock.UtcNow - saved.SavedAt >= _quietSaveInterval;
        bool hasMoved = handled.SequenceNumber != null && (handled.IncludesSeatStore || isQuietSaveDue);
        if (!isFinished && !hasMoved && delivery.Delayed is null)
        {
            return null;
        }

        var next = new ShardProgress
        {
            ShardId = delivery.ShardId,
            SequenceNumber = handled.SequenceNumber,
            IsFinished = isFinished,
            SavedAt = _clock.UtcNow,
            AlertedRequest = delivery.Delayed?.Request,
        };
        return KeepEarlierValues(next, saved);
    }

    // What this run did not change — no record handled, no new alert — stays as it was saved.
    private static ShardProgress KeepEarlierValues(ShardProgress next, ShardProgress? saved)
    {
        if (saved is null)
        {
            return next;
        }

        if (next.SequenceNumber is null)
        {
            next = next with { SequenceNumber = saved.SequenceNumber };
        }

        if (next.AlertedRequest is null)
        {
            next = next with { AlertedRequest = saved.AlertedRequest };
        }

        return next;
    }

    // Everything up to the first change not yet delivered is done; the shard's position may move that far.
    private static HandledRange HandledUpTo(List<StreamEntry> entries, HashSet<string> handledGroups)
    {
        string? sequenceNumber = null;
        bool includesSeatStore = false;
        foreach (StreamEntry entry in entries)
        {
            bool isWaiting = entry.Change != null && !handledGroups.Contains(SeatEvents.KeyOf(entry));
            if (isWaiting)
            {
                break;
            }

            sequenceNumber = entry.SequenceNumber;
            includesSeatStore = includesSeatStore || !entry.IsPublisherItem;
        }

        return new HandledRange(sequenceNumber, includesSeatStore);
    }

    private static PublishSeatChangesResult ToResult(List<ShardDelivery> deliveries)
    {
        var delayed = new List<DelayedSeatChange>();
        var unreadable = new List<UnreadableSeatChange>();
        var heldBack = new List<HeldBackSeatChange>();
        int deliveredCount = 0;
        foreach (ShardDelivery delivery in deliveries)
        {
            deliveredCount += delivery.DeliveredCount;
            unreadable.AddRange(delivery.Unreadable);
            if (delivery.Delayed != null)
            {
                delayed.Add(delivery.Delayed);
            }

            if (delivery.Stuck != null)
            {
                heldBack.Add(new HeldBackSeatChange(delivery.ShardId, delivery.Stuck.Key, delivery.Failure!));
            }
        }

        return new PublishSeatChangesResult
        {
            IsSkipped = false,
            DeliveredCount = deliveredCount,
            Delayed = delayed,
            Unreadable = unreadable,
            HeldBack = heldBack,
        };
    }

    /// <summary>How far a shard's records are handled, and whether any of them is about the seat store rather than the publisher.</summary>
    /// <param name="SequenceNumber">The last handled record, or null when none was.</param>
    /// <param name="IncludesSeatStore">True when a handled record is not one of the publisher's own items.</param>
    private sealed record HandledRange(string? SequenceNumber, bool IncludesSeatStore);

    /// <summary>What one shard's delivery did, filled in as it goes.</summary>
    private sealed class ShardDelivery
    {
        public ShardDelivery(string shardId)
        {
            ShardId = shardId;
        }

        public string ShardId { get; }

        /// <summary>The groups delivered, or passed over as unreadable.</summary>
        public HashSet<string> Handled { get; } = new HashSet<string>(StringComparer.Ordinal);

        public List<UnreadableSeatChange> Unreadable { get; } = new List<UnreadableSeatChange>();

        public int DeliveredCount { get; set; }

        /// <summary>The group that could not be delivered, holding back the rest of the shard.</summary>
        public SeatChangeGroup? Stuck { get; set; }

        public Exception? Failure { get; set; }

        public DelayedSeatChange? Delayed { get; set; }
    }
}
