namespace Api.Features.Holds.CancelHold;

/// <summary>Why an admin cancels a booking's hold; kept for the audit log.</summary>
/// <param name="Note">Why the admin cancels the hold: 10 to 500 characters, not only spaces.</param>
public sealed record CancelHoldRequest(string? Note);

/// <summary>The seats a cancellation freed.</summary>
/// <param name="TripId">The trip identifier.</param>
/// <param name="BookingId">The booking whose hold was cancelled.</param>
/// <param name="ReleasedSeatNos">The seats this request released.</param>
public sealed record CancelHoldResponse(Guid TripId, Guid BookingId, IReadOnlyList<string> ReleasedSeatNos);

/// <summary>The hold an admin cancels, why, and who asked.</summary>
public sealed record HoldToCancel
{
    /// <summary>The trip.</summary>
    public required Guid TripId { get; init; }

    /// <summary>The booking whose Held seats are freed.</summary>
    public required Guid BookingId { get; init; }

    /// <summary>The admin's note, stored on every released seat for the audit record.</summary>
    public required string Note { get; init; }

    /// <summary>The sub claim: recorded as who made the change, for the audit record.</summary>
    public required string AdminUserId { get; init; }

    /// <summary>The request's correlation identifier, stored on every released seat.</summary>
    public required string CorrelationId { get; init; }
}

/// <summary>How a cancellation request ended.</summary>
public enum CancelHoldOutcome
{
    Cancelled,
    TripNotFound,
    HoldNotActive,
    HoldAlreadyConfirmed,
}

/// <summary>The outcome, the released seats on success, and the booked seats a refusal names.</summary>
public sealed record CancelHoldResult
{
    /// <summary>How the request ended.</summary>
    public required CancelHoldOutcome Outcome { get; init; }

    /// <summary>The seats released, when cancelled.</summary>
    public CancelHoldResponse? Response { get; init; }

    /// <summary>The booking's Booked seats, for an already-confirmed refusal.</summary>
    public IReadOnlyList<string> BookedSeats { get; init; } = Array.Empty<string>();
}

/// <summary>Checks the body: a note of 10 to 500 characters that is not only spaces.</summary>
public sealed class CancelHoldValidator : AbstractValidator<CancelHoldRequest>
{
    public CancelHoldValidator()
    {
        RuleFor(x => x.Note)
            .Must(HoldCancellationRules.IsValidNote)
            .WithMessage("note must say why, in 10 to 500 characters.");
    }
}

[ApiController]
[ApiExplorerSettings(GroupName = "Holds")]
[Route("trips/{tripId}/holds/{bookingId}/cancel")]
public sealed class CancelHoldController : ControllerBase
{
    private readonly CancelHoldHandler _handler;
    private readonly CancelHoldValidator _validator = new();

    public CancelHoldController(IAmazonDynamoDB dynamoDb, IConfiguration configuration, IClock clock)
    {
        // Present by the time any request arrives: startup refuses to run without it.
        string tableName = configuration[Infrastructure.ConfigKeys.SeatStoreTableName]!;
        _handler = new CancelHoldHandler(dynamoDb, tableName, clock);
    }

    /// <summary>Free a booking's Held seats on a trip, with a note, leaving an audit record (Admin).</summary>
    /// <param name="tripId">The trip identifier (UUID).</param>
    /// <param name="bookingId">The booking identifier (UUID).</param>
    /// <param name="request">Why the hold is cancelled.</param>
    /// <param name="cancellationToken">Cancelled when the caller goes away.</param>
    /// <response code="200">The hold is cancelled and its seats released.</response>
    /// <response code="400">The trip or booking identifier or the note is malformed (VALIDATION_FAILED).</response>
    /// <response code="401">No valid access token was presented (UNAUTHORIZED).</response>
    /// <response code="403">The caller is not an admin (FORBIDDEN).</response>
    /// <response code="404">No trip with this identifier is known (NOT_FOUND).</response>
    /// <response code="409">The booking holds nothing on the trip, or its seats are Booked; nothing changed (HOLD_NOT_ACTIVE, HOLD_ALREADY_CONFIRMED).</response>
    [Authorize(Roles = AccessRoles.Admin)]
    [HttpPost]
    [ProducesResponseType(typeof(CancelHoldResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CancelHold(
        string tripId, string bookingId, CancelHoldRequest request, CancellationToken cancellationToken)
    {
        ValidationResult validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!Guid.TryParse(tripId, out Guid tripGuid))
        {
            validation.Errors.Add(new ValidationFailure("tripId", "tripId must be a trip identifier (a UUID)."));
        }

        if (!Guid.TryParse(bookingId, out Guid bookingGuid))
        {
            validation.Errors.Add(new ValidationFailure("bookingId", "bookingId must be a booking identifier (a UUID)."));
        }

        if (!validation.IsValid)
        {
            return ProblemResult.ValidationFailed(this, validation.ToDictionary());
        }

        var hold = new HoldToCancel
        {
            TripId = tripGuid,
            BookingId = bookingGuid,
            Note = request.Note!,
            AdminUserId = User.FindFirst(TokenClaims.Subject)!.Value,
            CorrelationId = CorrelationIds.Read(Request),
        };
        CancelHoldResult result = await _handler.HandleAsync(hold, cancellationToken);
        return ToActionResult(tripId, result);
    }

    private IActionResult ToActionResult(string tripId, CancelHoldResult result)
    {
        switch (result.Outcome)
        {
            case CancelHoldOutcome.Cancelled:
                return Ok(result.Response);
            case CancelHoldOutcome.TripNotFound:
                return ProblemResult.NotFound(this, $"No trip with identifier '{tripId}' is known.");
            case CancelHoldOutcome.HoldNotActive:
                return ProblemResult.HoldNotActive(
                    this, "The booking holds no seats on this trip; its hold was released, expired or never existed. Nothing changed.");
            case CancelHoldOutcome.HoldAlreadyConfirmed:
                return ProblemResult.HoldAlreadyConfirmed(
                    this, "These seats are paid for; use Booking's cancellation, which owns the refund. Nothing changed.", result.BookedSeats);
            default:
                throw new InvalidOperationException($"Unhandled outcome '{result.Outcome}'.");
        }
    }
}
