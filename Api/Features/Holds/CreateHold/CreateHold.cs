namespace Api.Features.Holds.CreateHold;

/// <summary>The seats Booking wants held for one booking while the customer pays.</summary>
/// <param name="BookingId">The booking the seats are held for (Booking's identifier, a UUID).</param>
/// <param name="SeatNos">The seat labels to hold: 1 to 6, all different.</param>
/// <param name="HoldSeconds">The hold length in seconds; 600 when not given, never above the hold ceiling.</param>
public sealed record CreateHoldRequest(string? BookingId, List<string>? SeatNos, int? HoldSeconds);

/// <summary>A booking's hold on a trip.</summary>
public sealed record CreateHoldResponse
{
    /// <summary>The trip identifier.</summary>
    public required Guid TripId { get; init; }

    /// <summary>The booking the seats are held for.</summary>
    public required Guid BookingId { get; init; }

    /// <summary>The seats of the hold.</summary>
    public required IReadOnlyList<string> SeatNos { get; init; }

    /// <summary>Held until confirmed, then booked.</summary>
    public required HoldStatus Status { get; init; }

    /// <summary>When the hold ends unless confirmed; null once booked.</summary>
    public required DateTimeOffset? HoldExpiresAt { get; init; }
}

/// <summary>
/// The hold timer: one message per new hold, delivered on the hold-expiry queue at the hold expiry,
/// telling Seat Inventory to free the seats still Held by this booking with this expiry.
/// </summary>
public sealed record HoldExpiryTimer
{
    /// <summary>The trip the seats belong to.</summary>
    public required Guid TripId { get; init; }

    /// <summary>The booking that holds them.</summary>
    public required Guid BookingId { get; init; }

    /// <summary>
    /// The hold expiry exactly as stored on the seats, so the timer can match it character for
    /// character and never free a newer hold of the same booking.
    /// </summary>
    public required string HoldExpiresAt { get; init; }

    /// <summary>The seats of the hold, for log lines only: the seats themselves decide what is freed.</summary>
    public required IReadOnlyList<string> SeatNos { get; init; }

    /// <summary>The hold request's correlation identifier, carried into the release it causes.</summary>
    public required string CorrelationId { get; init; }
}

/// <summary>How a hold request ended.</summary>
public enum CreateHoldOutcome
{
    Held,
    AlreadyHeld,
    TripNotFound,
    TripCancelled,
    TripDeparted,
    UnknownSeats,
    SeatsUnavailable,
    BookingAlreadyHasSeats,
    HoldTooLong,
}

/// <summary>The outcome, the hold on success, and the facts a refusal names.</summary>
public sealed record CreateHoldResult
{
    /// <summary>How the request ended.</summary>
    public required CreateHoldOutcome Outcome { get; init; }

    /// <summary>The hold, new or already in place.</summary>
    public CreateHoldResponse? Response { get; init; }

    /// <summary>The unknown or unavailable seats, or the booking's existing seats, a refusal names.</summary>
    public IReadOnlyList<string> RefusedSeats { get; init; } = Array.Empty<string>();

    /// <summary>Whether the booking's existing seats are held or booked, for a booking-already-has-seats refusal.</summary>
    public HoldStatus ExistingHoldStatus { get; init; }

    /// <summary>When the trip departed, for a departed-trip refusal.</summary>
    public DateTimeOffset? DepartedAt { get; init; }

    /// <summary>The hold ceiling, for a hold-too-long refusal.</summary>
    public int MaxHoldSeconds { get; init; }
}

/// <summary>The hold one request is trying to make.</summary>
internal sealed record CreateHoldAttempt
{
    public required Guid TripId { get; init; }

    public required Guid BookingId { get; init; }

    public required List<string> SeatNos { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public required DateTimeOffset Now { get; init; }

    public required string CorrelationId { get; init; }

    /// <summary>The expiry as stored on the seats and carried by the timer, so the two always match exactly.</summary>
    public string ExpiresAtText
    {
        get
        {
            return ExpiresAt.UtcDateTime.ToString("O");
        }
    }
}

/// <summary>The settings a hold needs from the environment.</summary>
/// <param name="TableName">The seat store table.</param>
/// <param name="QueueUrl">The hold-expiry queue the timer is sent to.</param>
/// <param name="MaxHoldSeconds">The hold ceiling.</param>
public sealed record CreateHoldSettings(string TableName, string QueueUrl, int MaxHoldSeconds);

/// <summary>Checks the request's shape: a booking UUID, 1 to 6 different seats, and a positive length if any.</summary>
public sealed class CreateHoldValidator : AbstractValidator<CreateHoldRequest>
{
    public CreateHoldValidator()
    {
        RuleFor(x => x.BookingId)
            .Must(IsBookingIdentifier)
            .WithMessage("bookingId must be a booking identifier (a UUID).");
        RuleFor(x => x.SeatNos)
            .Must(SeatHoldRules.IsValidSeatList)
            .WithMessage($"Name 1 to {SeatHoldRules.MaxSeats} different seats.");
        RuleFor(x => x.HoldSeconds)
            .Must(SeatHoldRules.IsValidLength)
            .WithMessage("holdSeconds must be at least 1.");
    }

    private static bool IsBookingIdentifier(string? bookingId)
    {
        return Guid.TryParse(bookingId, out _);
    }
}

[ApiController]
[ApiExplorerSettings(GroupName = "Holds")]
[Route("internal/trips/{tripId}/holds")]
public sealed class CreateHoldController : ControllerBase
{
    private readonly CreateHoldHandler _handler;
    private readonly CreateHoldValidator _validator = new();

    public CreateHoldController(IAmazonDynamoDB dynamoDb, IAmazonSQS sqs, IConfiguration configuration, IClock clock)
    {
        // Present and valid by the time any request arrives: startup refuses to run otherwise.
        var settings = new CreateHoldSettings(
            configuration[Infrastructure.ConfigKeys.SeatStoreTableName]!,
            configuration[Infrastructure.ConfigKeys.HoldExpiryQueueUrl]!,
            SeatHoldRules.ToCeilingSeconds(configuration[Infrastructure.ConfigKeys.MaxHoldSeconds])!.Value);
        _handler = new CreateHoldHandler(dynamoDb, sqs, clock, settings);
    }

    /// <summary>Hold 1–6 seats of a trip for a booking, all or nothing (Booking).</summary>
    /// <param name="tripId">The trip identifier (UUID).</param>
    /// <param name="request">The booking, the seats and the hold length.</param>
    /// <param name="cancellationToken">Cancelled when the caller goes away.</param>
    /// <response code="201">The hold is created; every seat is Held by the booking.</response>
    /// <response code="200">The booking already holds exactly these seats; the existing hold is returned unchanged.</response>
    /// <response code="400">The trip identifier or the request is malformed (VALIDATION_FAILED).</response>
    /// <response code="401">The internal key is missing or wrong (UNAUTHORIZED).</response>
    /// <response code="404">No trip with this identifier is known (NOT_FOUND).</response>
    /// <response code="409">Some seats are not free, the booking already has other seats, or the trip is cancelled or departed (SEATS_UNAVAILABLE, BOOKING_ALREADY_HAS_SEATS, TRIP_CANCELLED, TRIP_DEPARTED).</response>
    /// <response code="422">Unknown seat labels, or a hold length above the ceiling (UNKNOWN_SEATS, HOLD_TOO_LONG).</response>
    [Authorize(AuthenticationSchemes = InternalKeyAuthenticationOptions.SchemeName)]
    [HttpPost]
    [ProducesResponseType(typeof(CreateHoldResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(CreateHoldResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> CreateHold(string tripId, CreateHoldRequest request, CancellationToken cancellationToken)
    {
        ValidationResult validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!Guid.TryParse(tripId, out Guid tripGuid))
        {
            validation.Errors.Add(new ValidationFailure("tripId", "tripId must be a trip identifier (a UUID)."));
        }

        if (!validation.IsValid)
        {
            return ProblemResult.ValidationFailed(this, validation.ToDictionary());
        }

        string correlationId = CorrelationIds.Read(Request);
        CreateHoldResult result = await _handler.HandleAsync(tripGuid, request, correlationId, cancellationToken);
        return ToActionResult(tripId, result);
    }

    private IActionResult ToActionResult(string tripId, CreateHoldResult result)
    {
        switch (result.Outcome)
        {
            case CreateHoldOutcome.Held:
                return Created($"/internal/trips/{tripId}/holds/{result.Response!.BookingId}", result.Response);
            case CreateHoldOutcome.AlreadyHeld:
                return Ok(result.Response);
            case CreateHoldOutcome.TripNotFound:
                return ProblemResult.NotFound(this, $"No trip with identifier '{tripId}' is known.");
            case CreateHoldOutcome.TripCancelled:
                return ProblemResult.TripCancelled(this, $"Trip '{tripId}' was cancelled; its seats cannot be held.");
            case CreateHoldOutcome.TripDeparted:
                return ProblemResult.TripDeparted(this, $"Trip '{tripId}' has departed.", result.DepartedAt!.Value);
            case CreateHoldOutcome.UnknownSeats:
                return ProblemResult.UnknownSeats(this, "The trip has no seats with these labels; nothing was held.", result.RefusedSeats);
            case CreateHoldOutcome.SeatsUnavailable:
                return ProblemResult.SeatsUnavailable(this, "These seats are held, booked or blocked; nothing was held.", result.RefusedSeats);
            case CreateHoldOutcome.BookingAlreadyHasSeats:
                return ProblemResult.BookingAlreadyHasSeats(
                    this, "Release the booking's existing hold first, or change its seats; nothing was held.",
                    result.RefusedSeats, result.ExistingHoldStatus);
            case CreateHoldOutcome.HoldTooLong:
                return ProblemResult.HoldTooLong(
                    this, $"Ask for at most {result.MaxHoldSeconds} seconds; nothing was held.", result.MaxHoldSeconds);
            default:
                throw new InvalidOperationException($"Unhandled outcome '{result.Outcome}'.");
        }
    }
}
