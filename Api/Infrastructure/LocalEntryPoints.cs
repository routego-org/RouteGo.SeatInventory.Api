namespace Api.Infrastructure;

/// <summary>
/// The one place a developer sets what their machine points at — the local entry points for this
/// app's outbound configuration. Kept in C# rather than an appsettings.{Environment}.json so there
/// is a single list to read and no second file in which a key can be set here but forgotten there.
/// The values are seeded into the process environment block, so local runs and the deployed app
/// read configuration through the one identical channel (references/configuration.md).
/// Never put a real secret here — it would ship inside the assembly. Secrets stay in user-secrets
/// locally and Secrets Manager deployed, through SecretResolver (references/secrets.md).
/// </summary>
public static class LocalEntryPoints
{
    // The literal names, because this runs before there is an IHostEnvironment to ask: the
    // framework never exposes them as constants (WebHostDefaults.EnvironmentKey is the config key,
    // not the variable). The web stacks prefer ASPNETCORE_ENVIRONMENT; the generic host the lambda
    // worker builds reads only DOTNET_ENVIRONMENT — checking both mirrors the framework's own
    // resolution, so the guard behaves identically on every stack.
    private const string AspNetCoreEnvironmentVariable = "ASPNETCORE_ENVIRONMENT";
    private const string DotNetEnvironmentVariable = "DOTNET_ENVIRONMENT";

    private const string AwsRegion = "ap-south-1";

    /// <summary>
    /// Seeds the local values when this process runs as Development, and does nothing otherwise.
    /// MUST be called before the host builder is created: the builder's environment-variable
    /// provider snapshots the process block once, so a value set after that never reaches
    /// configuration.
    /// </summary>
    public static void Apply()
    {
        string? environmentName = Environment.GetEnvironmentVariable(AspNetCoreEnvironmentVariable)
            ?? Environment.GetEnvironmentVariable(DotNetEnvironmentVariable);
        if (environmentName != Environments.Development)
        {
            return;
        }

        Environment.SetEnvironmentVariable(ConfigKeys.AwsRegion, AwsRegion);

        // ══════════════════════════════════════════════════════════════════════
        // FILLED BY the sibling slice skill and by feature work: one line per key
        // in ConfigKeys, with the value this developer's machine should use — all of
        // it inside this seam (a field declared above it fails the foundation audit).
        //
        //   Environment.SetEnvironmentVariable(ConfigKeys.BackendBaseUrl, "http://localhost:5088/");
        //
        // A base URL carrying a path MUST end in '/' — see RequireBaseAddress in
        // ConfigurationValidation for what happens when it does not.
        // ══════════════════════════════════════════════════════════════════════
        Environment.SetEnvironmentVariable(ConfigKeys.SeatStoreTableName, "routego-seat-inventory");
        // DynamoDB Local in Docker: docker run -p 8000:8000 amazon/dynamodb-local
        Environment.SetEnvironmentVariable(ConfigKeys.DynamoDbServiceUrl, "http://localhost:8000");
        // RouteGo.Identity.Api running locally — the same issuer and audience its own LocalEntryPoints sets.
        Environment.SetEnvironmentVariable(ConfigKeys.IdentityIssuer, "http://localhost:5001");
        Environment.SetEnvironmentVariable(ConfigKeys.IdentityAudience, "routego");
        Environment.SetEnvironmentVariable(ConfigKeys.IdentityJwksUrl, "http://localhost:5001/.well-known/jwks.json");
        // LocalStack in Docker: awslocal sqs create-queue --queue-name inventory-hold-expiry-q
        Environment.SetEnvironmentVariable(ConfigKeys.SqsServiceUrl, "http://localhost:4566");
        Environment.SetEnvironmentVariable(
            ConfigKeys.HoldExpiryQueueUrl, "http://localhost:4566/000000000000/inventory-hold-expiry-q");
        // The same LocalStack; scripts/local-setup.sh creates both topics, the queue and the table's stream.
        Environment.SetEnvironmentVariable(ConfigKeys.SnsServiceUrl, "http://localhost:4566");
        Environment.SetEnvironmentVariable(
            ConfigKeys.InventoryEventsTopicArn, "arn:aws:sns:ap-south-1:000000000000:routego-inventory-events");
        Environment.SetEnvironmentVariable(
            ConfigKeys.AuditEventsTopicArn, "arn:aws:sns:ap-south-1:000000000000:routego-audit-events");
        // Fleet's three events, each on its own queue subscribed to routego-fleet-events — local-setup.sh again.
        Environment.SetEnvironmentVariable(
            ConfigKeys.TripScheduledQueueUrl, "http://localhost:4566/000000000000/inventory-trip-scheduled-q");
        Environment.SetEnvironmentVariable(
            ConfigKeys.TripScheduledDeadLetterQueueUrl, "http://localhost:4566/000000000000/inventory-trip-scheduled-dlq");
        Environment.SetEnvironmentVariable(
            ConfigKeys.TripCancelledQueueUrl, "http://localhost:4566/000000000000/inventory-trip-cancelled-q");
        Environment.SetEnvironmentVariable(
            ConfigKeys.TripCancelledDeadLetterQueueUrl, "http://localhost:4566/000000000000/inventory-trip-cancelled-dlq");
        Environment.SetEnvironmentVariable(
            ConfigKeys.FareChangedQueueUrl, "http://localhost:4566/000000000000/inventory-fare-changed-q");
        Environment.SetEnvironmentVariable(
            ConfigKeys.FareChangedDeadLetterQueueUrl, "http://localhost:4566/000000000000/inventory-fare-changed-dlq");
        // Booking's BookingFailed and BookingCancelled share one queue subscribed to routego-booking-events.
        Environment.SetEnvironmentVariable(
            ConfigKeys.BookingEventsQueueUrl, "http://localhost:4566/000000000000/inventory-booking-q");
        Environment.SetEnvironmentVariable(
            ConfigKeys.BookingEventsDeadLetterQueueUrl, "http://localhost:4566/000000000000/inventory-booking-dlq");
    }
}
