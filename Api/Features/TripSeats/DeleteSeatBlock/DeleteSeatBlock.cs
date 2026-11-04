namespace Api.Features.TripSeats.DeleteSeatBlock;

/// <summary>The seats an operator wants back on sale on one of its trips.</summary>
/// <param name="TripId">The trip identifier, from the route (a UUID).</param>
/// <param name="SeatNos">The seat labels to unblock, from the comma-separated query: 1 to 60, all different.</param>
public sealed record DeleteSeatBlockRequest(string TripId, List<string>? SeatNos);

/// <summary>The unblock one request asks for, and who asks.</summary>
public sealed record SeatUnblockTarget
{
    /// <summary>The trip.</summary>
    public required Guid TripId { get; init; }

    /// <summary>The seats to unblock.</summary>
    public required List<string> SeatNos { get; init; }

    /// <summary>The operatorId claim: the operator the user acts for.</summary>
    public required string? OperatorId { get; init; }

    /// <summary>The sub claim: recorded as who made the change, for the audit record.</summary>
    public required string UserId { get; init; }

    /// <summary>The request's correlation identifier, stored on every changed seat.</summary>
    public required string CorrelationId { get; init; }
}

/// <summary>How an unblock request ended.</summary>
public enum DeleteSeatBlockOutcome
{
    Unblocked,
    TripNotFound,
    NotTheTripsOperator,
    TripCancelled,
    TripDeparted,
    UnknownSeats,
    SeatsNotOperatorBlocked,
}

/// <summary>The outcome and the facts a refusal names.</summary>
public sealed record DeleteSeatBlockResult
{
    /// <summary>How the request ended.</summary>
    public required DeleteSeatBlockOutcome Outcome { get; init; }

    /// <summary>The unknown or not-operator-blocked seats a refusal names.</summary>
    public IReadOnlyList<string> RefusedSeats { get; init; } = Array.Empty<string>();

    /// <summary>When the trip departed, for a departed-trip refusal.</summary>
    public DateTimeOffset? DepartedAt { get; init; }
}

/// <summary>Checks the request's shape: a trip UUID and 1 to 60 different, non-blank seat labels.</summary>
public sealed class DeleteSeatBlockValidator : AbstractValidator<DeleteSeatBlockRequest>
{
    public DeleteSeatBlockValidator()
    {
        RuleFor(x => x.TripId)
            .Must(IsIdentifier)
            .WithMessage("tripId must be a trip identifier (a UUID).");
        RuleFor(x => x.SeatNos)
            .Must(SeatBlockRules.IsValidSeatList)
            .WithMessage($"Name 1 to {SeatBlockRules.MaxSeats} different seats.");
    }

    private static bool IsIdentifier(string identifier)
    {
        return Guid.TryParse(identifier, out _);
    }
}

[ApiController]
[ApiExplorerSettings(GroupName = "TripSeats")]
[Route("trips/{tripId}/seatBlocks")]
public sealed class DeleteSeatBlockController : ControllerBase
{
    private readonly DeleteSeatBlockHandler _handler;
    private readonly DeleteSeatBlockValidator _validator = new();

    public DeleteSeatBlockController(IAmazonDynamoDB dynamoDb, IConfiguration configuration, IClock clock)
    {
        // Present by the time any request arrives: startup refuses to run without it.
        string tableName = configuration[Infrastructure.ConfigKeys.SeatStoreTableName]!;
        _handler = new DeleteSeatBlockHandler(dynamoDb, tableName, clock);
    }

    /// <summary>Unblock seats the operator blocked on one of its trips, all or none (the trip's Operator).</summary>
    /// <param name="tripId">The trip identifier (UUID).</param>
    /// <param name="seatNos">The seat labels to unblock, comma-separated (1 to 60, all different).</param>
    /// <param name="cancellationToken">Cancelled when the caller goes away.</param>
    /// <response code="204">The seats are unblocked (or were already Available).</response>
    /// <response code="400">The trip identifier or the seat list is malformed (VALIDATION_FAILED).</response>
    /// <response code="401">No valid access token was presented (UNAUTHORIZED).</response>
    /// <response code="403">The caller is not an operator, or the trip belongs to another operator (FORBIDDEN).</response>
    /// <response code="404">No trip with this identifier is known (NOT_FOUND).</response>
    /// <response code="409">A seat was not blocked by the operator, or the trip is cancelled or departed (SEATS_NOT_OPERATOR_BLOCKED, TRIP_CANCELLED, TRIP_DEPARTED).</response>
    /// <response code="422">The trip has no seat with some of the labels given (UNKNOWN_SEATS).</response>
    [Authorize(Roles = AccessRoles.Operator)]
    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> DeleteSeatBlock(
        string tripId, [FromQuery] string? seatNos, CancellationToken cancellationToken)
    {
        var request = new DeleteSeatBlockRequest(tripId, ToSeatList(seatNos));
        ValidationResult validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ProblemResult.ValidationFailed(this, validation.ToDictionary());
        }

        var unblock = new SeatUnblockTarget
        {
            TripId = Guid.Parse(tripId),
            SeatNos = request.SeatNos!,
            OperatorId = User.FindFirst(TokenClaims.OperatorId)?.Value,
            UserId = User.FindFirst(TokenClaims.Subject)!.Value,
            CorrelationId = CorrelationIds.Read(Request),
        };
        DeleteSeatBlockResult result = await _handler.HandleAsync(unblock, cancellationToken);
        return ToActionResult(tripId, result);
    }

    // The contract sends the labels as one comma-separated value: "L1,L2". Nothing given means no list.
    private static List<string>? ToSeatList(string? seatNos)
    {
        if (seatNos is null)
        {
            return null;
        }

        string[] labels = seatNos.Split(',');
        return new List<string>(labels);
    }

    private IActionResult ToActionResult(string tripId, DeleteSeatBlockResult result)
    {
        switch (result.Outcome)
        {
            case DeleteSeatBlockOutcome.Unblocked:
                return NoContent();
            case DeleteSeatBlockOutcome.TripNotFound:
                return ProblemResult.NotFound(this, $"No trip with identifier '{tripId}' is known.");
            case DeleteSeatBlockOutcome.NotTheTripsOperator:
                return ProblemResult.Forbidden(this, $"Trip '{tripId}' belongs to another operator.");
            case DeleteSeatBlockOutcome.TripCancelled:
                return ProblemResult.TripCancelled(this, $"Trip '{tripId}' was cancelled; its seats stay blocked.");
            case DeleteSeatBlockOutcome.TripDeparted:
                return ProblemResult.TripDeparted(this, $"Trip '{tripId}' has departed.", result.DepartedAt!.Value);
            case DeleteSeatBlockOutcome.UnknownSeats:
                return ProblemResult.UnknownSeats(this, "The trip has no seats with these labels; nothing was unblocked.", result.RefusedSeats);
            case DeleteSeatBlockOutcome.SeatsNotOperatorBlocked:
                return ProblemResult.SeatsNotOperatorBlocked(
                    this, "These seats are held, booked or blocked by the trip's cancellation; nothing was unblocked.", result.RefusedSeats);
            default:
                throw new InvalidOperationException($"Unhandled outcome '{result.Outcome}'.");
        }
    }
}
