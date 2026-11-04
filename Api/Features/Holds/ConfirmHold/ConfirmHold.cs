namespace Api.Features.Holds.ConfirmHold;

/// <summary>Which booking's hold, on which trip, Booking says is paid.</summary>
/// <param name="TripId">The trip identifier, from the route (a UUID).</param>
/// <param name="BookingId">The booking identifier, from the route (a UUID).</param>
public sealed record ConfirmHoldRequest(string TripId, string BookingId);

/// <summary>A booking's hold on a trip, now booked.</summary>
public sealed record ConfirmHoldResponse
{
    /// <summary>The trip identifier.</summary>
    public required Guid TripId { get; init; }

    /// <summary>The booking the seats belong to.</summary>
    public required Guid BookingId { get; init; }

    /// <summary>The seats of the hold.</summary>
    public required IReadOnlyList<string> SeatNos { get; init; }

    /// <summary>Always booked once confirmed.</summary>
    public required HoldStatus Status { get; init; }

    /// <summary>Null: a booked hold no longer expires.</summary>
    public required DateTimeOffset? HoldExpiresAt { get; init; }
}

/// <summary>How a confirm request ended.</summary>
public enum ConfirmHoldOutcome
{
    Confirmed,
    TripNotFound,
    TripCancelled,
    HoldNotActive,
}

/// <summary>The outcome and, when confirmed, the booked hold.</summary>
/// <param name="Outcome">How the request ended.</param>
/// <param name="Response">The booked hold, when confirmed.</param>
public sealed record ConfirmHoldResult(ConfirmHoldOutcome Outcome, ConfirmHoldResponse? Response);

/// <summary>Checks the route: both identifiers are UUIDs.</summary>
public sealed class ConfirmHoldValidator : AbstractValidator<ConfirmHoldRequest>
{
    public ConfirmHoldValidator()
    {
        RuleFor(x => x.TripId)
            .Must(IsIdentifier)
            .WithMessage("tripId must be a trip identifier (a UUID).");
        RuleFor(x => x.BookingId)
            .Must(IsIdentifier)
            .WithMessage("bookingId must be a booking identifier (a UUID).");
    }

    private static bool IsIdentifier(string identifier)
    {
        return Guid.TryParse(identifier, out _);
    }
}

[ApiController]
[ApiExplorerSettings(GroupName = "Holds")]
[Route("internal/trips/{tripId}/holds/{bookingId}/confirm")]
public sealed class ConfirmHoldController : ControllerBase
{
    private readonly ConfirmHoldHandler _handler;
    private readonly ConfirmHoldValidator _validator = new();

    public ConfirmHoldController(IAmazonDynamoDB dynamoDb, IConfiguration configuration, IClock clock)
    {
        // Present by the time any request arrives: startup refuses to run without it.
        string tableName = configuration[Infrastructure.ConfigKeys.SeatStoreTableName]!;
        _handler = new ConfirmHoldHandler(dynamoDb, tableName, clock);
    }

    /// <summary>Turn a paid hold's seats from Held to Booked (Booking).</summary>
    /// <param name="tripId">The trip identifier (UUID).</param>
    /// <param name="bookingId">The booking identifier (UUID).</param>
    /// <param name="cancellationToken">Cancelled when the caller goes away.</param>
    /// <response code="200">The booking's seats on the trip are Booked.</response>
    /// <response code="400">The trip or booking identifier is malformed (VALIDATION_FAILED).</response>
    /// <response code="401">The internal key is missing or wrong (UNAUTHORIZED).</response>
    /// <response code="404">No trip with this identifier is known (NOT_FOUND).</response>
    /// <response code="409">The booking holds nothing on the trip, or the trip is cancelled (HOLD_NOT_ACTIVE, TRIP_CANCELLED).</response>
    [Authorize(AuthenticationSchemes = InternalKeyAuthenticationOptions.SchemeName)]
    [HttpPost]
    [ProducesResponseType(typeof(ConfirmHoldResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ConfirmHold(string tripId, string bookingId, CancellationToken cancellationToken)
    {
        var request = new ConfirmHoldRequest(tripId, bookingId);
        ValidationResult validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ProblemResult.ValidationFailed(this, validation.ToDictionary());
        }

        string correlationId = CorrelationIds.Read(Request);
        ConfirmHoldResult result = await _handler.HandleAsync(request, correlationId, cancellationToken);
        return ToActionResult(tripId, bookingId, result);
    }

    private IActionResult ToActionResult(string tripId, string bookingId, ConfirmHoldResult result)
    {
        switch (result.Outcome)
        {
            case ConfirmHoldOutcome.Confirmed:
                return Ok(result.Response);
            case ConfirmHoldOutcome.TripNotFound:
                return ProblemResult.NotFound(this, $"No trip with identifier '{tripId}' is known.");
            case ConfirmHoldOutcome.TripCancelled:
                return ProblemResult.TripCancelled(this, $"Trip '{tripId}' was cancelled; its hold cannot be confirmed.");
            case ConfirmHoldOutcome.HoldNotActive:
                return ProblemResult.HoldNotActive(
                    this, $"Booking '{bookingId}' holds no seats on trip '{tripId}': released, expired, or never held.");
            default:
                throw new InvalidOperationException($"Unhandled outcome '{result.Outcome}'.");
        }
    }
}
