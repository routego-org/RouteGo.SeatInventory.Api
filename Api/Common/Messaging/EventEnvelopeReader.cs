using System.Globalization;

namespace Api.Common.Messaging;

/// <summary>
/// Reads the RouteGo envelope from a raw queue message body. A body without the fields every worker
/// relies on — event ID, event type, the time it occurred, correlation ID and a data object — is
/// unusable and is reported, not guessed at.
/// </summary>
internal static class EventEnvelopeReader
{
    private static readonly string[] _textFields = { "eventId", "eventType", "correlationId" };

    public static EventEnvelopeReading Read(string body)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(body);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return new EventEnvelopeReading(null, "the body is not JSON");
        }

        string? missing = DescribeMissingField(root);
        if (missing != null)
        {
            return new EventEnvelopeReading(null, $"the envelope has no usable {missing}");
        }

        var receivedEvent = new ReceivedEvent
        {
            EventId = root.GetProperty("eventId").GetString()!,
            EventType = root.GetProperty("eventType").GetString()!,
            OccurredAt = ReadTime(root.GetProperty("occurredAt"))!.Value,
            CorrelationId = root.GetProperty("correlationId").GetString()!,
            Data = root.GetProperty("data"),
        };
        return new EventEnvelopeReading(receivedEvent, null);
    }

    private static string? DescribeMissingField(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return "object";
        }

        foreach (string field in _textFields)
        {
            if (!HasText(root, field))
            {
                return field;
            }
        }

        if (!root.TryGetProperty("occurredAt", out JsonElement occurredAt) || ReadTime(occurredAt) is null)
        {
            return "occurredAt";
        }

        if (!root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object)
        {
            return "data";
        }

        return null;
    }

    private static bool HasText(JsonElement root, string field)
    {
        if (!root.TryGetProperty(field, out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(value.GetString());
    }

    private static DateTimeOffset? ReadTime(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        bool isTime = DateTimeOffset.TryParse(
            value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset time);
        if (!isTime)
        {
            return null;
        }

        return time.ToUniversalTime();
    }
}

/// <summary>An envelope read from a queue message: the event, or why there is none.</summary>
/// <param name="Event">The event; null when the body cannot be used.</param>
/// <param name="Problem">Why the body cannot be used; null when it can.</param>
internal sealed record EventEnvelopeReading(ReceivedEvent? Event, string? Problem);
