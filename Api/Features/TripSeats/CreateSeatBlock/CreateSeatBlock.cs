namespace Api.Features.TripSeats.CreateSeatBlock;

/// <summary>The seats an operator wants kept off sale on one of its trips.</summary>
/// <param name="SeatNos">The seat labels to block: 1 to 60, all different.</param>
public sealed record CreateSeatBlockRequest(List<string>? SeatNos);

/// <summary>The seats now blocked by the operator.</summary>
/// <param name="TripId">The trip identifier.</param>
/// <param name="SeatNos">Every seat named in the request; all are now blocked by the operator.</param>
/// <param name="BlockedSeatNos">The seats this request newly blocked (empty when nothing changed).</param>
public sealed record CreateSeatBlockResponse(Guid TripId, IReadOnlyList<string> SeatNos, IReadOnlyList<string> BlockedSeatNos);

/// <summary>The signed-in operator user making the block, and the request it comes in.</summary>
/// <param name="OperatorId">The operatorId claim: the operator the user acts for.</param>
/// <param name="UserId">The sub claim: recorded as who made the change, for the audit record.</param>
/// <param name="CorrelationId">The request's correlation identifier, stored on every changed seat.</param>
public sealed record SeatBlockCaller(string? OperatorId, string? UserId, string CorrelationId);

/// <summary>How a block request ended.</summary>
public enum CreateSeatBlockOutcome
{
    Blocked,
    NothingToChange,
    TripNotFound,
    NotTheTripsOperator,
    TripCancelled,
    TripDeparted,
    UnknownSeats,
    SeatsUnavailable,
}

/// <summary>The outcome, the answer on success, and the facts a refusal names.</summary>
public sealed record CreateSeatBlockResult
{
    /// <summary>How the request ended.</summary>
    public required CreateSeatBlockOutcome Outcome { get; init; }

    /// <summary>The answer when the seats are (now) blocked.</summary>
    public CreateSeatBlockResponse? Response { get; init; }

    /// <summary>The unknown or unavailable seats a refusal names.</summary>
    public IReadOnlyList<string> RefusedSeats { get; init; } = Array.Empty<string>();

    /// <summary>When the trip departed, for a departed-trip refusal.</summary>
    public DateTimeOffset? DepartedAt { get; init; }
}

/// <summary>Checks the request's shape: 1 to 60 different, non-blank seat labels.</summary>
public sealed class CreateSeatBlockValidator : AbstractValidator<CreateSeatBlockRequest>
{
    public CreateSeatBlockValidator()
    {
        RuleFor(x => x.SeatNos)
            .Must(SeatBlockRules.IsValidSeatList)
            .WithMessage($"Name 1 to {SeatBlockRules.MaxSeats} different seats.");
    }
}

[ApiController]
[ApiExplorerSettings(GroupName = "TripSeats")]
[Route("trips/{tripId}/seatBlocks")]
public sealed class CreateSeatBlockController : ControllerBase
{
    private readonly CreateSeatBlockHandler _handler;
    private readonly CreateSeatBlockValidator _validator = new();

    public CreateSeatBlockController(IAmazonDynamoDB dynamoDb, IConfiguration configuration, IClock clock)
    {
        // Present by the time any request arrives: startup refuses to run without it.
        string tableName = configuration[Infrastructure.ConfigKeys.SeatStoreTableName]!;
        _handler = new CreateSeatBlockHandler(dynamoDb, tableName, clock);
    }

    /// <summary>Block Available seats of one of the operator's trips, all or none (the trip's Operator).</summary>
    /// <param name="tripId">The trip identifier (UUID).</param>
    /// <param name="request">The seats to block.</param>
    /// <param name="cancellationToken">Cancelled when the caller goes away.</param>
    /// <response code="201">The seats are blocked; at least one changed.</response>
    /// <response code="200">Every seat was already blocked by the operator; nothing changed.</response>
    /// <response code="400">The trip identifier or the seat list is malformed (VALIDATION_FAILED).</response>
    /// <response code="401">No valid access token was presented (UNAUTHORIZED).</response>
    /// <response code="403">The caller is not an operator, or the trip belongs to another operator (FORBIDDEN).</response>
    /// <response code="404">No trip with this identifier is known (NOT_FOUND).</response>
    /// <response code="409">A seat is not free, or the trip is cancelled or departed (SEATS_UNAVAILABLE, TRIP_CANCELLED, TRIP_DEPARTED).</response>
    /// <response code="422">The trip has no seat with some of the labels given (UNKNOWN_SEATS).</response>
    [Authorize(Roles = AccessRoles.Operator)]
    [HttpPost]
    [ProducesResponseType(typeof(CreateSeatBlockResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(CreateSeatBlockResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> CreateSeatBlock(
        string tripId, CreateSeatBlockRequest request, CancellationToken cancellationToken)
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

        string? operatorId = User.FindFirst(TokenClaims.OperatorId)?.Value;
        string? userId = User.FindFirst(TokenClaims.Subject)?.Value;
        var caller = new SeatBlockCaller(operatorId, userId, CorrelationIds.Read(Request));
        CreateSeatBlockResult result = await _handler.HandleAsync(tripGuid, request, caller, cancellationToken);
        return ToActionResult(tripId, result);
    }

    private IActionResult ToActionResult(string tripId, CreateSeatBlockResult result)
    {
        switch (result.Outcome)
        {
            case CreateSeatBlockOutcome.Blocked:
                return Created($"/trips/{tripId}/seatAllocations", result.Response);
            case CreateSeatBlockOutcome.NothingToChange:
                return Ok(result.Response);
            case CreateSeatBlockOutcome.TripNotFound:
                return ProblemResult.NotFound(this, $"No trip with identifier '{tripId}' is known.");
            case CreateSeatBlockOutcome.NotTheTripsOperator:
                return ProblemResult.Forbidden(this, $"Trip '{tripId}' belongs to another operator.");
            case CreateSeatBlockOutcome.TripCancelled:
                return ProblemResult.TripCancelled(this, $"Trip '{tripId}' was cancelled; its seats cannot be blocked.");
            case CreateSeatBlockOutcome.TripDeparted:
                return ProblemResult.TripDeparted(this, $"Trip '{tripId}' has departed.", result.DepartedAt!.Value);
            case CreateSeatBlockOutcome.UnknownSeats:
                return ProblemResult.UnknownSeats(this, "The trip has no seats with these labels; nothing was blocked.", result.RefusedSeats);
            case CreateSeatBlockOutcome.SeatsUnavailable:
                return ProblemResult.SeatsUnavailable(this, "These seats are held, booked or blocked by the trip's cancellation; nothing was blocked.", result.RefusedSeats);
            default:
                throw new InvalidOperationException($"Unhandled outcome '{result.Outcome}'.");
        }
    }
}
