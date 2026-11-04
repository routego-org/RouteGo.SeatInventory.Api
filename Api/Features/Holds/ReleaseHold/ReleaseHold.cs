namespace Api.Features.Holds.ReleaseHold;

/// <summary>Which booking's hold, on which trip, Booking wants freed.</summary>
/// <param name="TripId">The trip identifier, from the route (a UUID).</param>
/// <param name="BookingId">The booking identifier, from the route (a UUID).</param>
public sealed record ReleaseHoldRequest(string TripId, string BookingId);

/// <summary>The seats a release freed.</summary>
/// <param name="TripId">The trip identifier.</param>
/// <param name="BookingId">The booking whose hold was released.</param>
/// <param name="ReleasedSeatNos">The seats this request released (empty when nothing was held).</param>
public sealed record ReleaseHoldResponse(Guid TripId, Guid BookingId, IReadOnlyList<string> ReleasedSeatNos);

/// <summary>How a release request ended.</summary>
public enum ReleaseHoldOutcome
{
    Released,
    TripNotFound,
    HoldAlreadyConfirmed,
}

/// <summary>The outcome, the released seats on success, and the booked seats a refusal names.</summary>
public sealed record ReleaseHoldResult
{
    /// <summary>How the request ended.</summary>
    public required ReleaseHoldOutcome Outcome { get; init; }

    /// <summary>The seats released, when released.</summary>
    public ReleaseHoldResponse? Response { get; init; }

    /// <summary>The booking's Booked seats, for an already-confirmed refusal.</summary>
    public IReadOnlyList<string> BookedSeats { get; init; } = Array.Empty<string>();
}

/// <summary>Checks the route: both identifiers are UUIDs.</summary>
public sealed class ReleaseHoldValidator : AbstractValidator<ReleaseHoldRequest>
{
    public ReleaseHoldValidator()
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
[Route("internal/trips/{tripId}/holds/{bookingId}/release")]
public sealed class ReleaseHoldController : ControllerBase
{
    private readonly ReleaseHoldHandler _handler;
    private readonly ReleaseHoldValidator _validator = new();

    public ReleaseHoldController(IAmazonDynamoDB dynamoDb, IConfiguration configuration, IClock clock)
    {
        // Present by the time any request arrives: startup refuses to run without it.
        string tableName = configuration[Infrastructure.ConfigKeys.SeatStoreTableName]!;
        _handler = new ReleaseHoldHandler(dynamoDb, tableName, clock);
    }

    /// <summary>Free a booking's Held seats at once (Booking).</summary>
    /// <param name="tripId">The trip identifier (UUID).</param>
    /// <param name="bookingId">The booking identifier (UUID).</param>
    /// <param name="cancellationToken">Cancelled when the caller goes away.</param>
    /// <response code="200">The booking's Held seats are released (possibly none).</response>
    /// <response code="400">The trip or booking identifier is malformed (VALIDATION_FAILED).</response>
    /// <response code="401">The internal key is missing or wrong (UNAUTHORIZED).</response>
    /// <response code="404">No trip with this identifier is known (NOT_FOUND).</response>
    /// <response code="409">The booking's seats are Booked; nothing changed (HOLD_ALREADY_CONFIRMED).</response>
    [Authorize(AuthenticationSchemes = InternalKeyAuthenticationOptions.SchemeName)]
    [HttpPost]
    [ProducesResponseType(typeof(ReleaseHoldResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ReleaseHold(string tripId, string bookingId, CancellationToken cancellationToken)
    {
        var request = new ReleaseHoldRequest(tripId, bookingId);
        ValidationResult validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ProblemResult.ValidationFailed(this, validation.ToDictionary());
        }

        string correlationId = CorrelationIds.Read(Request);
        ReleaseHoldResult result = await _handler.HandleAsync(request, correlationId, cancellationToken);
        return ToActionResult(tripId, result);
    }

    private IActionResult ToActionResult(string tripId, ReleaseHoldResult result)
    {
        switch (result.Outcome)
        {
            case ReleaseHoldOutcome.Released:
                return Ok(result.Response);
            case ReleaseHoldOutcome.TripNotFound:
                return ProblemResult.NotFound(this, $"No trip with identifier '{tripId}' is known.");
            case ReleaseHoldOutcome.HoldAlreadyConfirmed:
                return ProblemResult.HoldAlreadyConfirmed(
                    this, "These seats are paid for; only Booking's cancellation frees them. Nothing changed.", result.BookedSeats);
            default:
                throw new InvalidOperationException($"Unhandled outcome '{result.Outcome}'.");
        }
    }
}
