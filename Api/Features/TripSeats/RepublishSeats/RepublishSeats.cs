namespace Api.Features.TripSeats.RepublishSeats;

/// <summary>Which trips to re-publish: the ones named, or every upcoming one.</summary>
public sealed record RepublishSeatsRequest
{
    /// <summary>The trips to re-publish; empty when <see cref="IsAllUpcoming"/> is set.</summary>
    public IReadOnlyList<Guid> TripIds { get; init; } = Array.Empty<Guid>();

    /// <summary>True to re-publish every known trip that has not departed and is not cancelled.</summary>
    public bool IsAllUpcoming { get; init; }

    /// <summary>The correlation identifier the run's events carry; one is generated when absent.</summary>
    public string? CorrelationId { get; init; }
}

/// <summary>What happened to one trip in a run.</summary>
public enum RepublishTripOutcome
{
    /// <summary>Its taken seats were all sent (possibly none, when no seat was taken).</summary>
    Republished,

    /// <summary>Skipped: it has already departed.</summary>
    Departed,

    /// <summary>Skipped: Fleet cancelled it.</summary>
    Cancelled,

    /// <summary>Skipped: Seat Inventory has no seats for it.</summary>
    NotKnown,

    /// <summary>Its events could not all be sent.</summary>
    Failed,
}

/// <summary>One trip's line in the run's report.</summary>
public sealed record RepublishTripReport
{
    public required Guid TripId { get; init; }

    public required RepublishTripOutcome Outcome { get; init; }

    /// <summary>The events sent for the trip; 0 unless it was re-published.</summary>
    public int EventCount { get; init; }

    /// <summary>What went wrong, when the trip failed.</summary>
    public Exception? Failure { get; init; }
}

/// <summary>The report of one run.</summary>
public sealed record RepublishSeatsResult
{
    /// <summary>The correlation identifier every event of the run carries.</summary>
    public required string CorrelationId { get; init; }

    public required IReadOnlyList<RepublishTripReport> Trips { get; init; }

    /// <summary>True when any trip failed, so the run as a whole ends as failed.</summary>
    public bool HasFailures
    {
        get
        {
            return Trips.Any(trip => trip.Outcome == RepublishTripOutcome.Failed);
        }
    }
}

/// <summary>
/// What the platform engineer typed after the program name: <c>--republish-trips id,id</c> or
/// <c>--republish-all</c>, and optionally <c>--correlation-id text</c>. Anything else is not a re-publish
/// and the API starts as usual.
/// </summary>
public sealed record RepublishSeatsCommandLine
{
    public const string TripsOption = "--republish-trips";
    public const string AllOption = "--republish-all";
    public const string CorrelationOption = "--correlation-id";

    /// <summary>True when a re-publish option was given, so the API runs the job instead of serving.</summary>
    public bool IsRequested { get; init; }

    /// <summary>The run to start; null when nothing was requested or the options are wrong.</summary>
    public RepublishSeatsRequest? Request { get; init; }

    /// <summary>What is wrong with the options, for the platform engineer to read.</summary>
    public string? Problem { get; init; }

    /// <summary>Reads the program arguments.</summary>
    public static RepublishSeatsCommandLine Parse(string[] args)
    {
        int tripsAt = Array.IndexOf(args, TripsOption);
        bool isAll = args.Contains(AllOption);
        if (tripsAt < 0 && !isAll)
        {
            return new RepublishSeatsCommandLine();
        }

        if (tripsAt >= 0 && isAll)
        {
            return Rejected($"Give either {TripsOption} or {AllOption}, not both.");
        }

        string? correlationId = ValueAfter(args, CorrelationOption);
        if (isAll)
        {
            var allRequest = new RepublishSeatsRequest { IsAllUpcoming = true, CorrelationId = correlationId };
            return new RepublishSeatsCommandLine { IsRequested = true, Request = allRequest };
        }

        return ParseTrips(ValueAfter(args, TripsOption), correlationId);
    }

    private static RepublishSeatsCommandLine ParseTrips(string? text, string? correlationId)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Rejected($"{TripsOption} needs one or more trip identifiers, separated by commas.");
        }

        var tripIds = new List<Guid>();
        foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Guid.TryParse(part, out Guid tripId))
            {
                return Rejected($"'{part}' is not a trip identifier.");
            }

            tripIds.Add(tripId);
        }

        var request = new RepublishSeatsRequest { TripIds = tripIds, CorrelationId = correlationId };
        return new RepublishSeatsCommandLine { IsRequested = true, Request = request };
    }

    private static string? ValueAfter(string[] args, string option)
    {
        int at = Array.IndexOf(args, option);
        if (at < 0 || at + 1 >= args.Length)
        {
            return null;
        }

        return args[at + 1];
    }

    private static RepublishSeatsCommandLine Rejected(string problem)
    {
        return new RepublishSeatsCommandLine { IsRequested = true, Problem = problem };
    }
}

/// <summary>
/// Runs the re-publish from the command line and writes its report to the console. The exit code is 0
/// when every chosen trip was re-published or skipped, 1 when any trip failed (so a pipeline or the
/// platform engineer's shell sees the run failed and can re-run the failed trips), and 2 when the
/// options are wrong.
/// </summary>
public static class RepublishSeatsRunner
{
    private const int Succeeded = 0;
    private const int Failed = 1;
    private const int NotStarted = 2;

    public static async Task<int> RunAsync(IServiceProvider services, RepublishSeatsCommandLine commandLine, CancellationToken cancellationToken)
    {
        if (commandLine.Request is null)
        {
            await Console.Error.WriteLineAsync(commandLine.Problem);
            return NotStarted;
        }

        RepublishSeatsHandler handler = services.GetRequiredService<RepublishSeatsHandler>();
        RepublishSeatsResult result = await handler.HandleAsync(commandLine.Request, cancellationToken);
        await Console.Out.WriteLineAsync($"Seat re-publish, correlation {result.CorrelationId}:");
        foreach (RepublishTripReport trip in result.Trips)
        {
            await Console.Out.WriteLineAsync("  " + Describe(trip));
        }

        if (result.HasFailures)
        {
            return Failed;
        }

        return Succeeded;
    }

    private static string Describe(RepublishTripReport trip)
    {
        switch (trip.Outcome)
        {
            case RepublishTripOutcome.Republished:
                return $"{trip.TripId}: re-published with {trip.EventCount} events";
            case RepublishTripOutcome.Departed:
                return $"{trip.TripId}: skipped, departed";
            case RepublishTripOutcome.Cancelled:
                return $"{trip.TripId}: skipped, cancelled";
            case RepublishTripOutcome.NotKnown:
                return $"{trip.TripId}: skipped, not known";
            default:
                return $"{trip.TripId}: FAILED, {trip.Failure?.Message}";
        }
    }
}
