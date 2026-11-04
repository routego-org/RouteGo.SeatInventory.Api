namespace Api.Common.Messaging;

/// <summary>
/// One event as it arrived on a queue, read from the RouteGo envelope
/// (<c>eventId, eventType, version, occurredAt, correlationId, producer, data</c>). The data stays raw
/// JSON: each worker reads it into its own message record, because the shape belongs to the event.
/// </summary>
public sealed record ReceivedEvent
{
    /// <summary>The producer's identifier for the event; a repeat delivery carries the same one.</summary>
    public required string EventId { get; init; }

    public required string EventType { get; init; }

    /// <summary>When the producer says the event happened, UTC.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>The correlation identifier of the request that caused the event, carried on to every change it makes.</summary>
    public required string CorrelationId { get; init; }

    public required JsonElement Data { get; init; }
}
