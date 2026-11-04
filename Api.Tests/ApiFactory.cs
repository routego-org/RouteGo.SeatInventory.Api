using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Api.Tests;

/// <summary>
/// Boots the real host in-process so a slice test drives the app the way a caller does — routing,
/// model binding, validation, the wire contract, the error pipeline and every middleware all run.
/// Written once per project (skills-aspnetcore-project-setup/references/test-host.md); slices
/// consume it and never build their own.
/// <para>
/// Two lines here are load-bearing, and both fail silently rather than loudly when removed:
/// </para>
/// <para>
/// 1. <c>UseEnvironment(Environments.Development)</c>. The secret gate is
/// <c>IHostEnvironment.IsDevelopment()</c>. A host that does not say so runs as <b>Production</b>
/// and SecretResolver takes the Secrets Manager path — from CI, with no role
/// (references/secrets.md).
/// </para>
/// <para>
/// 2. The secret is injected as an <b>environment variable</b>, never through <c>UseSetting</c> or
/// <c>ConfigureAppConfiguration</c>. <c>WebApplication.CreateBuilder</c> re-applies its own sources
/// — user-secrets included — on top of anything a test appends, and a developer's user-secrets
/// mirror this very name. The environment-variable provider is registered AFTER user-secrets, so
/// it is the one source that deterministically wins.
/// </para>
/// <para>
/// <c>Environment.SetEnvironmentVariable</c> is process-global. Every factory in a run that injects
/// a given name must inject the same value for it, or the classes that disagree must be serialised
/// into one xunit collection.
/// </para>
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string? _secretName;
    private readonly string? _secretJson;
    private readonly Action<IServiceCollection>[] _overrides;

    /// <summary>A host that needs no secret — every dependency it would reach for is replaced.</summary>
    /// <param name="overrides">Test doubles to register over the host's own registrations.</param>
    public ApiFactory(params Action<IServiceCollection>[] overrides)
        : this(null, null, overrides)
    {
    }

    /// <param name="secretName">
    /// The app's one secret NAME — the same constant the composition root resolves, so the test
    /// exercises the real secret contract rather than a test-only config key.
    /// </param>
    /// <param name="secretJson">
    /// Its value, in the platform's standard JSON shape. The mapper that reads it in Development
    /// and on Lambda is the one under test here too.
    /// </param>
    /// <param name="overrides">Test doubles to register over the host's own registrations.</param>
    public ApiFactory(
        string? secretName, string? secretJson, params Action<IServiceCollection>[] overrides)
    {
        _secretName = secretName;
        _secretJson = secretJson;
        _overrides = overrides;

        // Required at startup, and the same for every host in the run. No test host publishes —
        // the background workers are removed below — so these topics never need to exist.
        Environment.SetEnvironmentVariable(
            "InventoryEventsTopicArn", "arn:aws:sns:ap-south-1:000000000000:inventory-events-tests");
        Environment.SetEnvironmentVariable("AuditEventsTopicArn", "arn:aws:sns:ap-south-1:000000000000:audit-events-tests");
        // The same for Fleet's and Booking's event queues: no test host reads them, so they never need to exist.
        Environment.SetEnvironmentVariable("TripScheduledQueueUrl", "http://localhost:4566/000000000000/trip-scheduled-tests");
        Environment.SetEnvironmentVariable("TripScheduledDeadLetterQueueUrl", "http://localhost:4566/000000000000/trip-scheduled-dlq-tests");
        Environment.SetEnvironmentVariable("TripCancelledQueueUrl", "http://localhost:4566/000000000000/trip-cancelled-tests");
        Environment.SetEnvironmentVariable("TripCancelledDeadLetterQueueUrl", "http://localhost:4566/000000000000/trip-cancelled-dlq-tests");
        Environment.SetEnvironmentVariable("FareChangedQueueUrl", "http://localhost:4566/000000000000/fare-changed-tests");
        Environment.SetEnvironmentVariable("FareChangedDeadLetterQueueUrl", "http://localhost:4566/000000000000/fare-changed-dlq-tests");
        Environment.SetEnvironmentVariable("BookingEventsQueueUrl", "http://localhost:4566/000000000000/booking-events-tests");
        Environment.SetEnvironmentVariable("BookingEventsDeadLetterQueueUrl", "http://localhost:4566/000000000000/booking-events-dlq-tests");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);

        if (!string.IsNullOrEmpty(_secretName))
        {
            Environment.SetEnvironmentVariable(_secretName, _secretJson);
        }

        builder.ConfigureTestServices(services =>
        {
            // The seat-event publisher and the queue workers would poll endpoints no test host has;
            // their own tests drive their handlers directly.
            var workers = services.Where(IsBackgroundWorker).ToList();
            foreach (ServiceDescriptor worker in workers)
            {
                services.Remove(worker);
            }

            foreach (Action<IServiceCollection> apply in _overrides)
            {
                apply(services);
            }
        });
    }

    private static bool IsBackgroundWorker(ServiceDescriptor descriptor)
    {
        return descriptor.ImplementationType == typeof(Api.Features.TripSeats.PublishSeatChanges.PublishSeatChangesService)
            || descriptor.ImplementationType == typeof(Api.Features.Holds.ExpireHold.ExpireHoldService)
            || typeof(Api.Common.Messaging.EventQueueService).IsAssignableFrom(descriptor.ImplementationType);
    }
}
