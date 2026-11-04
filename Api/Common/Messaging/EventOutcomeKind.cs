namespace Api.Common.Messaging;

/// <summary>What happens to an event's queue message once its worker has looked at it.</summary>
public enum EventOutcomeKind
{
    /// <summary>The event is applied, or was already: the message is deleted.</summary>
    Applied,

    /// <summary>The event can never be applied as it is: the message is set aside on the dead-letter queue at once.</summary>
    Rejected,

    /// <summary>The event cannot be applied yet: the message stays, and the queue delivers it again later.</summary>
    Postponed,
}
