namespace Api.Infrastructure;

/// <summary>
/// The name of every environment-supplied configuration value, spelled exactly once. The names are
/// deliberately FLAT: each maps 1:1 to an environment variable of the same spelling, so no ':' or
/// '__' separator can be mistyped between the deployment template, the pipeline, and the reader
/// here. LocalEntryPoints and every consumer go through these constants, so a rename is a compile
/// error rather than a value that silently arrives null (references/configuration.md).
/// </summary>
public static class ConfigKeys
{
    /// <summary>The AWS SDK's own region variable, and the ONLY region source the app reads.
    /// Lambda sets it; LocalEntryPoints seeds it on a laptop. There is deliberately no app-owned
    /// AWS:Region key that could disagree with the runtime's own value.</summary>
    public const string AwsRegion = "AWS_REGION";

    // ══════════════════════════════════════════════════════════════════════════
    // FILLED BY the sibling slice skill and by feature work: one const per value
    // that varies by environment — a base URL, a client id, a public origin.
    //
    //   public const string BackendBaseUrl = "BackendBaseUrl";
    //
    // Adding one is four more edits, none of them a new file: a local value in
    // LocalEntryPoints, a parameter + env var in the deployment template, a
    // pipeline variable, and a RequireKeys entry if the app cannot start without
    // it. NEVER add a configuration key to an appsettings*.json.
    //
    // Otel:Exporter:* and AWS:Profile do NOT belong here — see
    // references/configuration.md for why those two keep their nested names.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>The DynamoDB table holding every trip and its seats. Set per deployment.</summary>
    public const string SeatStoreTableName = "SeatStoreTableName";

    /// <summary>A DynamoDB endpoint to use instead of AWS's own — DynamoDB Local on a developer's
    /// machine or in tests. Left unset when deployed, so the SDK's regional endpoint applies.</summary>
    public const string DynamoDbServiceUrl = "DynamoDbServiceUrl";

    /// <summary>The issuer Identity writes into every access token; a token from anyone else is refused.</summary>
    public const string IdentityIssuer = "IdentityIssuer";

    /// <summary>The audience Identity writes into every access token.</summary>
    public const string IdentityAudience = "IdentityAudience";

    /// <summary>Where Identity publishes the public keys its access tokens are signed with.</summary>
    public const string IdentityJwksUrl = "IdentityJwksUrl";

    /// <summary>The queue that delivers each new hold's timer back to Seat Inventory at its hold expiry.</summary>
    public const string HoldExpiryQueueUrl = "HoldExpiryQueueUrl";

    /// <summary>An SQS endpoint to use instead of AWS's own — LocalStack on a developer's machine.
    /// Left unset when deployed, so the SDK's regional endpoint applies.</summary>
    public const string SqsServiceUrl = "SqsServiceUrl";

    /// <summary>The SNS topic every seat event (SeatHeld, SeatBooked, …) is published to — the
    /// "inventory events stream" Search and Booking subscribe to.</summary>
    public const string InventoryEventsTopicArn = "InventoryEventsTopicArn";

    /// <summary>The SNS topic AuditRecorded is published to — RouteGo's "audit events stream",
    /// shared with Identity.</summary>
    public const string AuditEventsTopicArn = "AuditEventsTopicArn";

    /// <summary>An SNS endpoint to use instead of AWS's own — LocalStack on a developer's machine.
    /// Left unset when deployed, so the SDK's regional endpoint applies.</summary>
    public const string SnsServiceUrl = "SnsServiceUrl";

    /// <summary>The queue subscribed to Fleet's events stream that delivers TripScheduled only.</summary>
    public const string TripScheduledQueueUrl = "TripScheduledQueueUrl";

    /// <summary>Where a TripScheduled that can never be applied is set aside — the trip-scheduled queue's dead-letter queue.</summary>
    public const string TripScheduledDeadLetterQueueUrl = "TripScheduledDeadLetterQueueUrl";

    /// <summary>The queue subscribed to Fleet's events stream that delivers TripCancelled only.</summary>
    public const string TripCancelledQueueUrl = "TripCancelledQueueUrl";

    /// <summary>Where a TripCancelled that can never be applied is set aside — the trip-cancelled queue's dead-letter queue.</summary>
    public const string TripCancelledDeadLetterQueueUrl = "TripCancelledDeadLetterQueueUrl";

    /// <summary>The queue subscribed to Fleet's events stream that delivers FareChanged only.</summary>
    public const string FareChangedQueueUrl = "FareChangedQueueUrl";

    /// <summary>Where a FareChanged that can never be applied is set aside — the fare-changed queue's dead-letter queue.</summary>
    public const string FareChangedDeadLetterQueueUrl = "FareChangedDeadLetterQueueUrl";

    /// <summary>The queue subscribed to Booking's events stream that delivers BookingFailed and BookingCancelled.</summary>
    public const string BookingEventsQueueUrl = "BookingEventsQueueUrl";

    /// <summary>Where a Booking event that can never be applied is set aside — the booking queue's dead-letter queue.</summary>
    public const string BookingEventsDeadLetterQueueUrl = "BookingEventsDeadLetterQueueUrl";

    /// <summary>The longest hold Booking may ask for, in seconds. Optional: 900 when unset, and never
    /// above 900, the longest delay the hold timer's queue can give a message.</summary>
    public const string MaxHoldSeconds = "MaxHoldSeconds";
}
