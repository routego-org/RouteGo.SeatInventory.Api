using Api.Features.Holds.ReleaseCancelledBooking;
using Api.Features.Holds.ReleaseFailedBooking;

namespace Api.Infrastructure.Messaging;

/// <summary>
/// Hands each Booking event from the one booking queue to the slice that owns its event type: BookingFailed
/// to ReleaseFailedBooking, BookingCancelled to ReleaseCancelledBooking.
/// </summary>
public sealed class BookingEventDispatcher
{
    /// <summary>Every event type the booking queue carries — the same list as its subscription filter.</summary>
    public static readonly IReadOnlyList<string> EventTypes = new List<string>
    {
        ReleaseFailedBookingHandler.TriggerEventType,
        ReleaseCancelledBookingHandler.TriggerEventType,
    };

    private readonly ReleaseFailedBookingHandler _releaseFailedBooking;
    private readonly ReleaseCancelledBookingHandler _releaseCancelledBooking;

    public BookingEventDispatcher(ReleaseFailedBookingHandler releaseFailedBooking, ReleaseCancelledBookingHandler releaseCancelledBooking)
    {
        _releaseFailedBooking = releaseFailedBooking;
        _releaseCancelledBooking = releaseCancelledBooking;
    }

    /// <summary>Applies one event whose type is one of <see cref="EventTypes"/>.</summary>
    public async Task<EventOutcome> DispatchAsync(ReceivedEvent receivedEvent, CancellationToken cancellationToken)
    {
        if (receivedEvent.EventType == ReleaseFailedBookingHandler.TriggerEventType)
        {
            return await _releaseFailedBooking.HandleAsync(receivedEvent, cancellationToken);
        }

        // The queue lets through only EventTypes, so anything else is BookingCancelled.
        return await _releaseCancelledBooking.HandleAsync(receivedEvent, cancellationToken);
    }
}
