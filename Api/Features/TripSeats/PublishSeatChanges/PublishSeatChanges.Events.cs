namespace Api.Features.TripSeats.PublishSeatChanges;

/// <summary>
/// Names the events a request's seat changes announce: groups the changes back into one group per
/// request, and turns each group into its seat event — plus, for a staff action, its audit record —
/// in the RouteGo envelope. No I/O, no clock: the same changes always give the same events, with the
/// same event identifiers, which is what lets subscribers ignore a repeat.
/// </summary>
internal static class SeatEvents
{
    private const string AuditEventType = "AuditRecorded";
    private const string TripEntity = "Trip";
    private const string Booked = "Booked";

    // Changes Search and Booking hear about from Fleet's own events, so they announce nothing.
    private static readonly string[] _silentReasons = { "TripCancelled", "FareChanged" };

    /// <summary>
    /// The seat changes in the order their requests first appear, one group per request. A change
    /// with no request of its own stays a group by itself, so it can be reported, not merged.
    /// </summary>
    public static List<SeatChangeGroup> Group(List<StreamEntry> entries)
    {
        var groups = new List<SeatChangeGroup>();
        var groupByKey = new Dictionary<string, SeatChangeGroup>(StringComparer.Ordinal);
        foreach (StreamEntry entry in entries)
        {
            if (entry.Change is null)
            {
                continue;
            }

            string key = KeyOf(entry);
            if (!groupByKey.TryGetValue(key, out SeatChangeGroup? group))
            {
                group = new SeatChangeGroup(key, entry.RecordedAt, new List<SeatChange>());
                groupByKey[key] = group;
                groups.Add(group);
            }

            group.Changes.Add(entry.Change);
        }

        return groups;
    }

    /// <summary>The group a seat change belongs to: its request, or its own position when it has none.</summary>
    public static string KeyOf(StreamEntry entry)
    {
        string? request = entry.Change!.Request;
        if (string.IsNullOrEmpty(request))
        {
            return "record:" + entry.SequenceNumber;
        }

        return request;
    }

    /// <summary>The trips whose operator a group's event names (operator blocks and unblocks).</summary>
    public static List<string> ListTripsNeedingOperator(List<SeatChangeGroup> groups)
    {
        var tripIds = new List<string>();
        foreach (SeatChangeGroup group in groups)
        {
            string? reason = group.Changes[0].Reason;
            bool isOperatorAction = reason == SeatChangeReasons.OperatorBlocked || reason == SeatChangeReasons.OperatorUnblocked;
            if (isOperatorAction && !tripIds.Contains(group.Changes[0].TripId))
            {
                tripIds.Add(group.Changes[0].TripId);
            }
        }

        return tripIds;
    }

    /// <summary>
    /// What a group announces: its events, nothing (a change subscribers hear of elsewhere), or —
    /// when the recorded change cannot be read — the problem, so it is reported instead of guessed at.
    /// </summary>
    public static SeatEventPlan Plan(SeatChangeGroup group, Dictionary<string, string> operatorByTrip)
    {
        SeatChange first = group.Changes[0];
        string? problem = FindCommonProblem(group);
        if (problem != null)
        {
            return SeatEventPlan.Unreadable(problem);
        }

        if (_silentReasons.Contains(first.Reason))
        {
            return SeatEventPlan.Nothing();
        }

        switch (first.Reason)
        {
            case SeatChangeReasons.Held:
                return PlanHeld(group);
            case SeatChangeReasons.Booked:
                return PlanBooked(group);
            case SeatChangeReasons.OperatorBlocked:
                return PlanOperatorAction(group, operatorByTrip, "SeatBlocked", "SeatsBlocked");
            case SeatChangeReasons.OperatorUnblocked:
                return PlanOperatorAction(group, operatorByTrip, "SeatUnblocked", "SeatsUnblocked");
            case SeatChangeReasons.SeatChanged:
                return PlanSeatChanged(group);
            default:
                return PlanReleased(group);
        }
    }

    // What every event needs, whatever the reason: one known reason, the request as an event
    // identity, the correlation identifier and the time of the change.
    private static string? FindCommonProblem(SeatChangeGroup group)
    {
        SeatChange first = group.Changes[0];
        if (!Guid.TryParse(first.Request, out _))
        {
            return $"the change request '{first.Request}' is not an identifier";
        }

        if (first.CorrelationId is null || first.ChangedAt is null || first.Status is null)
        {
            return "the change has no correlation identifier, change time or status";
        }

        bool isKnown = _silentReasons.Contains(first.Reason) || SeatChangeReasons.IsAnnounced(first.Reason);
        if (!isKnown)
        {
            return $"the change reason '{first.Reason}' is not one this publisher knows";
        }

        if (!group.Changes.TrueForAll(change => change.Reason == first.Reason))
        {
            return "one request recorded different change reasons";
        }

        return null;
    }

    private static SeatEventPlan PlanHeld(SeatChangeGroup group)
    {
        SeatChange first = group.Changes[0];
        if (first.BookingId is null || first.HoldExpiresAt is null)
        {
            return SeatEventPlan.Unreadable("a held seat has no booking or hold expiry");
        }

        var data = new SeatHeldData
        {
            TripId = first.TripId,
            BookingId = first.BookingId,
            SeatNos = SeatNosOf(group),
            HoldExpiresAt = first.HoldExpiresAt,
        };
        return SeatEventPlan.Announce(SeatEvent(group, "SeatHeld", data));
    }

    private static SeatEventPlan PlanBooked(SeatChangeGroup group)
    {
        SeatChange first = group.Changes[0];
        if (first.BookingId is null)
        {
            return SeatEventPlan.Unreadable("a booked seat has no booking");
        }

        var data = new SeatBookedData { TripId = first.TripId, BookingId = first.BookingId, SeatNos = SeatNosOf(group) };
        return SeatEventPlan.Announce(SeatEvent(group, "SeatBooked", data));
    }

    // Every release reason gives SeatReleased; the booking it freed is on the seat's old image,
    // because a release removes it. An admin's cancellation is also audited.
    private static SeatEventPlan PlanReleased(SeatChangeGroup group)
    {
        SeatChange first = group.Changes[0];
        if (first.PreviousBookingId is null)
        {
            return SeatEventPlan.Unreadable("a released seat had no booking before the release");
        }

        var data = new SeatReleasedData
        {
            TripId = first.TripId,
            BookingId = first.PreviousBookingId,
            SeatNos = SeatNosOf(group),
            Reason = first.Reason!,
            ReleasedTo = first.Status!,
        };
        SeatEventMessage released = SeatEvent(group, "SeatReleased", data);
        if (first.Reason != SeatChangeReasons.AdminReleased)
        {
            return SeatEventPlan.Announce(released);
        }

        var details = new AuditHoldDetails { SeatNos = data.SeatNos, BookingId = data.BookingId, Note = first.Note };
        return PlanWithAudit(group, released, new StaffAction("Admin", "HoldCancelled", details));
    }

    private static SeatEventPlan PlanOperatorAction(
        SeatChangeGroup group, Dictionary<string, string> operatorByTrip, string eventType, string auditAction)
    {
        SeatChange first = group.Changes[0];
        if (!operatorByTrip.TryGetValue(first.TripId, out string? operatorId))
        {
            return SeatEventPlan.Unreadable("the trip has no operator");
        }

        var data = new SeatBlockData { TripId = first.TripId, SeatNos = SeatNosOf(group), OperatorId = operatorId };
        SeatEventMessage seatEvent = SeatEvent(group, eventType, data);
        var action = new StaffAction("Operator", auditAction, new AuditSeatsDetails { SeatNos = data.SeatNos });
        return PlanWithAudit(group, seatEvent, action);
    }

    // A move frees the old seat and books the new one; the new seat names the one it replaced.
    private static SeatEventPlan PlanSeatChanged(SeatChangeGroup group)
    {
        List<SeatChange> bookedSeats = group.Changes.FindAll(change => change.Status == Booked);
        if (bookedSeats.Count == 0 || bookedSeats.Exists(change => change.MovedFrom is null || change.BookingId is null))
        {
            return SeatEventPlan.Unreadable("a changed seat does not name the booking or the seat it replaced");
        }

        var data = new SeatChangedData
        {
            TripId = bookedSeats[0].TripId,
            BookingId = bookedSeats[0].BookingId!,
            Changes = bookedSeats.ConvertAll(change => new SeatMove(change.MovedFrom!, change.SeatNo)),
        };
        return SeatEventPlan.Announce(SeatEvent(group, "SeatChanged", data));
    }

    private static SeatEventPlan PlanWithAudit(SeatChangeGroup group, SeatEventMessage seatEvent, StaffAction action)
    {
        SeatChange first = group.Changes[0];
        if (first.ChangedBy is null)
        {
            return SeatEventPlan.Unreadable("a staff action does not name the staff user");
        }

        var audit = new AuditRecordedData
        {
            ActorId = first.ChangedBy,
            ActorRole = action.ActorRole,
            Action = action.Action,
            EntityType = TripEntity,
            EntityId = first.TripId,
            Details = action.Details,
        };
        // The audit record needs an identity of its own that is still the same on every repeat, so it
        // is derived from the request rather than drawn at random.
        Guid auditEventId = SeatEventEnvelopes.DeriveEventId(group.Key, AuditEventType);
        SeatEventMessage auditEvent = SeatEventEnvelopes.ToMessage(SeatEventStream.Audit, AuditEventType, auditEventId, StampOf(first), audit);
        return SeatEventPlan.Announce(seatEvent, auditEvent);
    }

    private static SeatEventMessage SeatEvent(SeatChangeGroup group, string eventType, object data)
    {
        // The request identifier is the event identity, so the same change always gives the same event.
        var eventId = Guid.Parse(group.Key);
        return SeatEventEnvelopes.ToMessage(SeatEventStream.Inventory, eventType, eventId, StampOf(group.Changes[0]), data);
    }

    private static EventStamp StampOf(SeatChange first)
    {
        return new EventStamp(first.ChangedAt!, first.CorrelationId!);
    }

    private static List<string> SeatNosOf(SeatChangeGroup group)
    {
        return group.Changes.ConvertAll(change => change.SeatNo);
    }
}

/// <summary>The reasons a seat change records, as the write slices store them.</summary>
internal static class SeatChangeReasons
{
    public const string Held = "Held";
    public const string Booked = "Booked";
    public const string AdminReleased = "AdminReleased";
    public const string OperatorBlocked = "OperatorBlocked";
    public const string OperatorUnblocked = "OperatorUnblocked";
    public const string SeatChanged = "SeatChanged";

    // Every reason a release can record; each gives SeatReleased with that reason.
    private static readonly string[] _releaseReasons = { "HoldReleased", "HoldExpired", "BookingFailed", "BookingCancelled", AdminReleased };

    /// <summary>True for a reason that announces an event.</summary>
    public static bool IsAnnounced(string? reason)
    {
        bool isOther = reason == Held || reason == Booked || reason == OperatorBlocked || reason == OperatorUnblocked || reason == SeatChanged;
        return isOther || _releaseReasons.Contains(reason);
    }
}

/// <summary>Who did a staff action and what it was, for its audit record.</summary>
/// <param name="ActorRole">Admin or Operator.</param>
/// <param name="Action">HoldCancelled, SeatsBlocked or SeatsUnblocked.</param>
/// <param name="Details">The action's details: the seats, and for a hold cancellation the booking and the note.</param>
internal sealed record StaffAction(string ActorRole, string Action, object Details);

/// <summary>The seat changes one request made, in the order the stream recorded them.</summary>
/// <param name="Key">The request (or, for an unreadable change, the record) the group is for.</param>
/// <param name="FirstRecordedAt">When the first of its changes was recorded.</param>
/// <param name="Changes">The changes, one per seat.</param>
internal sealed record SeatChangeGroup(string Key, DateTimeOffset FirstRecordedAt, List<SeatChange> Changes);

/// <summary>What a group announces: its events, or the problem that keeps it from being read.</summary>
internal sealed class SeatEventPlan
{
    private SeatEventPlan(List<SeatEventMessage> messages, string? problem)
    {
        Messages = messages;
        Problem = problem;
    }

    /// <summary>The events to deliver, in order; empty when there is nothing to announce.</summary>
    public List<SeatEventMessage> Messages { get; }

    /// <summary>Why the group cannot be read, or null when it can.</summary>
    public string? Problem { get; }

    public static SeatEventPlan Announce(params SeatEventMessage[] messages)
    {
        return new SeatEventPlan(new List<SeatEventMessage>(messages), null);
    }

    public static SeatEventPlan Nothing()
    {
        return new SeatEventPlan(new List<SeatEventMessage>(), null);
    }

    public static SeatEventPlan Unreadable(string problem)
    {
        return new SeatEventPlan(new List<SeatEventMessage>(), problem);
    }
}
