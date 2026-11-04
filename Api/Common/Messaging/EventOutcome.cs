namespace Api.Common.Messaging;

/// <summary>
/// What a worker made of one event. A rejected or postponed event names why, in the words the
/// platform engineer's alert carries.
/// </summary>
public sealed class EventOutcome
{
    /// <summary>The alert reason for event data that can never be applied.</summary>
    public const string InvalidEventData = "invalidEventData";

    /// <summary>The alert reason for an event still failing after its last delivery.</summary>
    public const string RepeatedFailure = "repeatedFailure";

    private EventOutcome(EventOutcomeKind kind, string? reason, string? problem)
    {
        Kind = kind;
        Reason = reason;
        Problem = problem;
    }

    public EventOutcomeKind Kind { get; }

    /// <summary>The alert reason, for a rejected or postponed event; null once applied.</summary>
    public string? Reason { get; }

    /// <summary>What exactly is wrong, naming the field or the record; null once applied.</summary>
    public string? Problem { get; }

    public static EventOutcome Applied()
    {
        return new EventOutcome(EventOutcomeKind.Applied, null, null);
    }

    /// <summary>The event's data is unusable; trying again cannot help.</summary>
    public static EventOutcome Rejected(string problem)
    {
        return new EventOutcome(EventOutcomeKind.Rejected, InvalidEventData, problem);
    }

    /// <summary>The event cannot be applied yet, for a reason a later delivery may find gone.</summary>
    public static EventOutcome Postponed(string reason, string problem)
    {
        return new EventOutcome(EventOutcomeKind.Postponed, reason, problem);
    }
}
