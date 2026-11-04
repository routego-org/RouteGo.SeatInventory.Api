namespace Api.Common.Messaging;

/// <summary>The queue one worker reads, and what it does with an event it cannot apply.</summary>
public sealed record EventQueue
{
    public required string QueueUrl { get; init; }

    /// <summary>Where an event that can never be applied is set aside at once, unchanged, for the platform engineer.</summary>
    public required string DeadLetterQueueUrl { get; init; }

    /// <summary>The event types this queue's subscription lets through; any other is set aside.</summary>
    public required IReadOnlyList<string> EventTypes { get; init; }

    /// <summary>The alert raised when one of its events is not applied.</summary>
    public required ProblemType NotAppliedProblem { get; init; }
}
