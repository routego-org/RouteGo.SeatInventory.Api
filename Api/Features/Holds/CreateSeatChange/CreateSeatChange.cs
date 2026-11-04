namespace Api.Features.Holds.CreateSeatChange;

/// <summary>One move as Booking sends it.</summary>
/// <param name="FromSeatNo">A seat Booked by the booking.</param>
/// <param name="ToSeatNo">An Available seat of the same seat class.</param>
public sealed record RequestedSeatMove(string? FromSeatNo, string? ToSeatNo);

/// <summary>The moves Booking wants for one booking on one trip, all together or none.</summary>
/// <param name="Changes">The moves: 1 to 6; no seat may appear twice, and a seat may not move to itself.</param>
public sealed record CreateSeatChangeRequest(List<RequestedSeatMove?>? Changes);

/// <summary>A booking's seats after a seat change.</summary>
public sealed record CreateSeatChangeResponse
{
    /// <summary>The trip identifier.</summary>
    public required Guid TripId { get; init; }

    /// <summary>The booking whose seats changed.</summary>
    public required Guid BookingId { get; init; }

    /// <summary>The moves of the request, done now or by an earlier identical request.</summary>
    public required IReadOnlyList<SeatMove> Changes { get; init; }

    /// <summary>The booking's seats on the trip after the change.</summary>
    public required IReadOnlyList<string> SeatNos { get; init; }
}

/// <summary>The seat change one request asks for.</summary>
public sealed record SeatChangeTarget
{
    /// <summary>The trip.</summary>
    public required Guid TripId { get; init; }

    /// <summary>The booking whose Booked seats move.</summary>
    public required Guid BookingId { get; init; }

    /// <summary>The moves, in the order Booking sent them.</summary>
    public required List<SeatMove> Moves { get; init; }

    /// <summary>The request's correlation identifier, stored on every changed seat.</summary>
    public required string CorrelationId { get; init; }
}

/// <summary>How a seat change request ended.</summary>
public enum CreateSeatChangeOutcome
{
    Changed,
    AlreadyChanged,
    TripNotFound,
    TripCancelled,
    TripDeparted,
    UnknownSeats,
    SeatsNotOwned,
    SeatClassMismatch,
    SeatsUnavailable,
}

/// <summary>The outcome, the answer on success, and the facts a refusal names.</summary>
public sealed record CreateSeatChangeResult
{
    /// <summary>How the request ended.</summary>
    public required CreateSeatChangeOutcome Outcome { get; init; }

    /// <summary>The booking's seats, when changed now or already.</summary>
    public CreateSeatChangeResponse? Response { get; init; }

    /// <summary>The unknown, not-owned or unavailable seats a refusal names.</summary>
    public IReadOnlyList<string> RefusedSeats { get; init; } = Array.Empty<string>();

    /// <summary>The moves to a seat of another class, for a class-mismatch refusal.</summary>
    public IReadOnlyList<SeatMove> RefusedMoves { get; init; } = Array.Empty<SeatMove>();

    /// <summary>When the trip departed, for a departed-trip refusal.</summary>
    public DateTimeOffset? DepartedAt { get; init; }
}

/// <summary>Checks the request's shape: 1 to 6 moves between named seats, no seat named twice, none to itself.</summary>
public sealed class CreateSeatChangeValidator : AbstractValidator<CreateSeatChangeRequest>
{
    public CreateSeatChangeValidator()
    {
        RuleFor(x => x.Changes)
            .Must(HaveOneToSixMoves)
            .WithMessage($"Name 1 to {SeatChangeRules.MaxMoves} moves.");
        RuleFor(x => x.Changes)
            .Must(NameEverySeatOnce)
            .WithMessage("No seat may appear twice in one seat change.");
        RuleForEach(x => x.Changes).ChildRules(move =>
        {
            move.RuleFor(m => m!.FromSeatNo)
                .Must(IsSeatLabel)
                .WithMessage("fromSeatNo must name a seat.");
            move.RuleFor(m => m!.ToSeatNo)
                .Must(IsSeatLabel)
                .WithMessage("toSeatNo must name a seat.");
            move.RuleFor(m => m!.ToSeatNo)
                .NotEqual(m => m!.FromSeatNo)
                .WithMessage("A seat cannot move to itself.");
        });
    }

    private static bool HaveOneToSixMoves(List<RequestedSeatMove?>? changes)
    {
        if (changes is null || changes.Exists(move => move is null))
        {
            return false;
        }

        return SeatChangeRules.IsValidMoveCount(changes.Count);
    }

    private static bool NameEverySeatOnce(List<RequestedSeatMove?>? changes)
    {
        // A missing or broken list is reported by the move-count rule alone.
        if (changes is null || changes.Exists(move => move is null))
        {
            return true;
        }

        var seatNos = new List<string>();
        foreach (RequestedSeatMove? move in changes)
        {
            seatNos.Add(move!.FromSeatNo ?? string.Empty);
            seatNos.Add(move.ToSeatNo ?? string.Empty);
        }

        return SeatChangeRules.IsEverySeatNamedOnce(seatNos);
    }

    private static bool IsSeatLabel(string? seatNo)
    {
        return !string.IsNullOrWhiteSpace(seatNo);
    }
}

[ApiController]
[ApiExplorerSettings(GroupName = "Holds")]
[Route("internal/trips/{tripId}/holds/{bookingId}/seatChanges")]
public sealed class CreateSeatChangeController : ControllerBase
{
    private readonly CreateSeatChangeHandler _handler;
    private readonly CreateSeatChangeValidator _validator = new();

    public CreateSeatChangeController(IAmazonDynamoDB dynamoDb, IConfiguration configuration, IClock clock)
    {
        // Present by the time any request arrives: startup refuses to run without it.
        string tableName = configuration[Infrastructure.ConfigKeys.SeatStoreTableName]!;
        _handler = new CreateSeatChangeHandler(dynamoDb, tableName, clock);
    }

    /// <summary>Move a booking from some of its Booked seats to Available seats of the same class, in one step (Booking).</summary>
    /// <param name="tripId">The trip identifier (UUID).</param>
    /// <param name="bookingId">The booking identifier (UUID).</param>
    /// <param name="request">The moves.</param>
    /// <param name="cancellationToken">Cancelled when the caller goes away.</param>
    /// <response code="201">The seats are changed.</response>
    /// <response code="200">Every move was already done; nothing changed.</response>
    /// <response code="400">The trip or booking identifier or the moves are malformed (VALIDATION_FAILED).</response>
    /// <response code="401">The internal key is missing or wrong (UNAUTHORIZED).</response>
    /// <response code="404">No trip with this identifier is known (NOT_FOUND).</response>
    /// <response code="409">A from-seat is not the booking's, a to-seat is not free, or the trip is cancelled or departed (SEATS_NOT_OWNED, SEATS_UNAVAILABLE, TRIP_CANCELLED, TRIP_DEPARTED).</response>
    /// <response code="422">Unknown seat labels, or a move to a different seat class (UNKNOWN_SEATS, SEAT_CLASS_MISMATCH).</response>
    [Authorize(AuthenticationSchemes = InternalKeyAuthenticationOptions.SchemeName)]
    [HttpPost]
    [ProducesResponseType(typeof(CreateSeatChangeResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(CreateSeatChangeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> CreateSeatChange(
        string tripId, string bookingId, CreateSeatChangeRequest request, CancellationToken cancellationToken)
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

        var change = new SeatChangeTarget
        {
            TripId = tripGuid,
            BookingId = bookingGuid,
            Moves = ToMoves(request.Changes!),
            CorrelationId = CorrelationIds.Read(Request),
        };
        CreateSeatChangeResult result = await _handler.HandleAsync(change, cancellationToken);
        return ToActionResult(tripId, result);
    }

    private static List<SeatMove> ToMoves(List<RequestedSeatMove?> changes)
    {
        var moves = new List<SeatMove>();
        foreach (RequestedSeatMove? change in changes)
        {
            moves.Add(new SeatMove(change!.FromSeatNo!, change.ToSeatNo!));
        }

        return moves;
    }

    private IActionResult ToActionResult(string tripId, CreateSeatChangeResult result)
    {
        switch (result.Outcome)
        {
            case CreateSeatChangeOutcome.Changed:
                return Created($"/trips/{tripId}/seatAllocations", result.Response);
            case CreateSeatChangeOutcome.AlreadyChanged:
                return Ok(result.Response);
            case CreateSeatChangeOutcome.TripNotFound:
                return ProblemResult.NotFound(this, $"No trip with identifier '{tripId}' is known.");
            case CreateSeatChangeOutcome.TripCancelled:
                return ProblemResult.TripCancelled(this, $"Trip '{tripId}' was cancelled; its seats cannot be changed.");
            case CreateSeatChangeOutcome.TripDeparted:
                return ProblemResult.TripDeparted(this, $"Trip '{tripId}' has departed.", result.DepartedAt!.Value);
            case CreateSeatChangeOutcome.UnknownSeats:
                return ProblemResult.UnknownSeats(this, "The trip has no seats with these labels; nothing moved.", result.RefusedSeats);
            case CreateSeatChangeOutcome.SeatsNotOwned:
                return ProblemResult.SeatsNotOwned(this, "These seats are not booked by this booking; nothing moved.", result.RefusedSeats);
            case CreateSeatChangeOutcome.SeatClassMismatch:
                return ProblemResult.SeatClassMismatch(this, "These moves go to a seat of another class; nothing moved.", result.RefusedMoves);
            case CreateSeatChangeOutcome.SeatsUnavailable:
                return ProblemResult.SeatsUnavailable(this, "These seats are held, booked or blocked; nothing moved.", result.RefusedSeats);
            default:
                throw new InvalidOperationException($"Unhandled outcome '{result.Outcome}'.");
        }
    }
}
