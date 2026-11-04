namespace Api.Common.Messaging;

/// <summary>
/// Delivers seat events to SNS: seat events to the inventory events topic, audit records to the
/// audit events topic, each with its event type as the eventType message attribute so subscribers
/// can filter without reading the body.
/// </summary>
public sealed class SeatEventSender
{
    private const string EventTypeAttribute = "eventType";
    private const string StringType = "String";

    private readonly IAmazonSimpleNotificationService _sns;
    private readonly string _inventoryTopicArn;
    private readonly string _auditTopicArn;

    public SeatEventSender(IAmazonSimpleNotificationService sns, IConfiguration configuration)
    {
        _sns = sns;
        // Present by the time the publisher runs: startup refuses to run without them.
        _inventoryTopicArn = configuration[Infrastructure.ConfigKeys.InventoryEventsTopicArn]!;
        _auditTopicArn = configuration[Infrastructure.ConfigKeys.AuditEventsTopicArn]!;
    }

    /// <summary>Publishes one event; throws when the messaging service does not take it.</summary>
    public async Task SendAsync(SeatEventMessage message, CancellationToken cancellationToken)
    {
        string topicArn = _inventoryTopicArn;
        if (message.Stream == SeatEventStream.Audit)
        {
            topicArn = _auditTopicArn;
        }

        var eventType = new Amazon.SimpleNotificationService.Model.MessageAttributeValue
        {
            DataType = StringType,
            StringValue = message.EventType,
        };
        var request = new Amazon.SimpleNotificationService.Model.PublishRequest
        {
            TopicArn = topicArn,
            Message = message.EnvelopeJson,
            MessageAttributes = new Dictionary<string, Amazon.SimpleNotificationService.Model.MessageAttributeValue>
            {
                { EventTypeAttribute, eventType },
            },
        };
        await _sns.PublishAsync(request, cancellationToken);
    }
}

/// <summary>Which of RouteGo's event streams an event belongs on.</summary>
public enum SeatEventStream
{
    Inventory,
    Audit,
}

/// <summary>One event ready to deliver.</summary>
/// <param name="Stream">Where it goes.</param>
/// <param name="EventType">Its event type, also sent as the eventType message attribute.</param>
/// <param name="EnvelopeJson">The whole RouteGo envelope, as JSON.</param>
public sealed record SeatEventMessage(SeatEventStream Stream, string EventType, string EnvelopeJson);

/// <summary>The RouteGo system envelope every event travels in.</summary>
internal sealed class EventEnvelope
{
    /// <summary>The same on every redelivery, so subscribers can ignore a repeat.</summary>
    public required Guid EventId { get; init; }

    public required string EventType { get; init; }

    /// <summary>The schema version of <see cref="Data"/>.</summary>
    public required int Version { get; init; }

    /// <summary>When the seat change happened, ISO-8601 UTC.</summary>
    public required string OccurredAt { get; init; }

    public required string CorrelationId { get; init; }

    public required string Producer { get; init; }

    public required object Data { get; init; }
}

/// <summary>SeatHeld: a new hold.</summary>
internal sealed class SeatHeldData
{
    public required string TripId { get; init; }

    public required string BookingId { get; init; }

    public required List<string> SeatNos { get; init; }

    public required string HoldExpiresAt { get; init; }

    /// <summary>True only on an event re-sent by the seat re-publish; absent on every real change.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Republished { get; init; }
}

/// <summary>SeatBooked: a confirmed hold.</summary>
internal sealed class SeatBookedData
{
    public required string TripId { get; init; }

    public required string BookingId { get; init; }

    public required List<string> SeatNos { get; init; }

    /// <summary>True only on an event re-sent by the seat re-publish; absent on every real change.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Republished { get; init; }
}

/// <summary>SeatReleased: a hold's seats freed, why, and whether they went back on sale.</summary>
internal sealed class SeatReleasedData
{
    public required string TripId { get; init; }

    public required string BookingId { get; init; }

    public required List<string> SeatNos { get; init; }

    /// <summary>HoldExpired, HoldReleased, BookingFailed, BookingCancelled or AdminReleased.</summary>
    public required string Reason { get; init; }

    /// <summary>Available, or Blocked when the trip is cancelled.</summary>
    public required string ReleasedTo { get; init; }
}

/// <summary>SeatBlocked and SeatUnblocked: seats an operator blocked or unblocked on its trip.</summary>
internal sealed class SeatBlockData
{
    public required string TripId { get; init; }

    public required List<string> SeatNos { get; init; }

    public required string OperatorId { get; init; }

    /// <summary>True only on an event re-sent by the seat re-publish; absent on every real change.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Republished { get; init; }
}

/// <summary>SeatChanged: a booking moved from some of its seats to others.</summary>
internal sealed class SeatChangedData
{
    public required string TripId { get; init; }

    public required string BookingId { get; init; }

    /// <summary>Each move: fromSeatNo and toSeatNo.</summary>
    public required List<SeatMove> Changes { get; init; }
}

/// <summary>AuditRecorded: a staff action, for the audit log.</summary>
internal sealed class AuditRecordedData
{
    /// <summary>The admin's or operator user's identifier.</summary>
    public required string ActorId { get; init; }

    /// <summary>Admin or Operator.</summary>
    public required string ActorRole { get; init; }

    /// <summary>HoldCancelled, SeatsBlocked or SeatsUnblocked.</summary>
    public required string Action { get; init; }

    public required string EntityType { get; init; }

    public required string EntityId { get; init; }

    public required object Details { get; init; }
}

/// <summary>An operator block's or unblock's audit details: the seats.</summary>
internal sealed class AuditSeatsDetails
{
    public required List<string> SeatNos { get; init; }
}

/// <summary>An admin hold cancellation's audit details: the seats, the booking and the admin's note.</summary>
internal sealed class AuditHoldDetails
{
    public required List<string> SeatNos { get; init; }

    public required string BookingId { get; init; }

    public string? Note { get; init; }
}

/// <summary>When an event's seats changed and which request it belongs to, for its envelope.</summary>
/// <param name="OccurredAt">ISO-8601 UTC.</param>
/// <param name="CorrelationId">The correlation identifier the envelope carries.</param>
internal sealed record EventStamp(string OccurredAt, string CorrelationId);

/// <summary>
/// How every seat event is wrapped and identified, whichever slice sends it: the RouteGo envelope,
/// its JSON, and an event identity that is the same every time it is derived from the same name.
/// </summary>
internal static class SeatEventEnvelopes
{
    private const string Producer = "seat-inventory-service";
    private const int EnvelopeVersion = 1;

    /// <summary>Wraps data in the RouteGo envelope and serializes it, ready to deliver on <paramref name="stream"/>.</summary>
    public static SeatEventMessage ToMessage(SeatEventStream stream, string eventType, Guid eventId, EventStamp stamp, object data)
    {
        var envelope = new EventEnvelope
        {
            EventId = eventId,
            EventType = eventType,
            Version = EnvelopeVersion,
            OccurredAt = stamp.OccurredAt,
            CorrelationId = stamp.CorrelationId,
            Producer = Producer,
            Data = data,
        };
        string json = JsonSerializer.Serialize(envelope, WireJson.Options);
        return new SeatEventMessage(stream, eventType, json);
    }

    /// <summary>An identifier that is the same every time it is derived from the same name and event type.</summary>
    public static Guid DeriveEventId(string name, string eventType)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(name + ":" + eventType);
        byte[] hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return new Guid(hash.AsSpan(0, 16));
    }
}
