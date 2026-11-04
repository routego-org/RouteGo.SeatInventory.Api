// Drop into <ProjectRoot>/Observability/AddObservabilityExtensions.cs.
//
// ONE wiring entry point shared by every project shape. Web API, MVC, and
// annotation-based Lambda all call AddObservability(...) so the resource,
// exporter, and instrumentation stay identical across services.
//
// Classic (LambdaEntryPoint) shapes call FlushTelemetry() explicitly in
// FunctionHandlerAsync. Minimal hosting (AddAWSLambdaHosting) shapes rely on
// the built-in OtelFlushService that hooks IHostApplicationLifetime.ApplicationStopping.
//
// Architecture:
//   Lambda: exports OTLP to the local ADOT Collector on localhost:4318.
//     The collector (running as a Lambda Layer/extension) handles auth and
//     forwards to the backend. collector.yaml is loaded from S3 via
//     OPENTELEMETRY_COLLECTOR_CONFIG_URI. App code never touches backend
//     credentials.
//   Local dev: exports OTLP directly to the backend (no collector sidecar).
//     Reads endpoint/headers from IConfiguration (Otel:Exporter:*).
//
// See references/exporter-config.md.

using System.Reflection;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Api.Observability;

/// <summary>
/// Registers vendor-neutral OpenTelemetry tracing and logging with a single
/// shared resource. In Lambda the OTLP exporter targets the local ADOT Collector
/// sidecar; in local dev it exports directly to the backend.
/// </summary>
public static class AddObservabilityExtensions
{
    private static TracerProvider? _tracerProvider;

    /// <summary>
    /// Wires traces and logs through OpenTelemetry over OTLP.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">App configuration.</param>
    /// <param name="environment">Host environment (for <c>deployment.environment</c>).</param>
    /// <param name="logging">The logging builder, so OTel logs share the same resource.</param>
    /// <param name="serviceName">OTel service name. Defaults to <see cref="Diagnostics.SourceName"/>.</param>
    /// <param name="configureTracing">Shape-specific tracing instrumentation (e.g. ASP.NET Core).</param>
    public static IServiceCollection AddObservability(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        ILoggingBuilder logging,
        string? serviceName = null,
        Action<TracerProviderBuilder>? configureTracing = null)
    {
        try
        {
            return ConfigureObservability(
                services, configuration, environment, logging,
                serviceName, configureTracing);
        }
        catch (Exception ex)
        {
            // OTel must NEVER crash the application. Log the error and continue
            // without telemetry — the app functions normally, just unobserved.
            Console.Error.WriteLine(
                $"[OTel] Observability setup failed — telemetry disabled: {ex.Message}");

            // Register a no-op provider so [FromServices] TracerProvider injection
            // in Lambda handlers doesn't fail.
            EnsureProvidersRegistered(services);
            return services;
        }
    }

    private static IServiceCollection ConfigureObservability(
        IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        ILoggingBuilder logging,
        string? serviceName,
        Action<TracerProviderBuilder>? configureTracing)
    {
        // Set W3C TraceContext + Baggage propagators so incoming traceparent/tracestate
        // headers from callers are extracted and outgoing HttpClient calls propagate them.
        // This enables distributed tracing across all services.
        Sdk.SetDefaultTextMapPropagator(
            new CompositeTextMapPropagator(new TextMapPropagator[]
            {
                new TraceContextPropagator(),
                new BaggagePropagator()
            }));

        bool isLambda = !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("AWS_EXECUTION_ENV"));

        string name = serviceName ?? Diagnostics.SourceName;
        string version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0";

        // In Lambda: use function name as service name for cleaner Elastic APM grouping.
        if (isLambda)
        {
            name = Environment.GetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME") ?? name;
        }

        string envName;
        if (isLambda)
        {
            envName = Environment.GetEnvironmentVariable("stage")
                ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                ?? "Production";
        }
        else
        {
            envName = environment.EnvironmentName;
        }

        // OTLP endpoints — route through local ADOT Collector in Lambda, direct to backend for dev
        string? otlpBaseUrl;
        if (isLambda)
        {
            otlpBaseUrl = "http://localhost:4318";
        }
        else
        {
            otlpBaseUrl = GetDirectExportBaseUrl(configuration);
        }

        // Local dev without Otel:Exporter:Endpoint — skip telemetry gracefully
        if (otlpBaseUrl is null)
        {
            Console.WriteLine(
                "[OTel] No Otel:Exporter:Endpoint configured — telemetry disabled for local dev.");
            EnsureProvidersRegistered(services);
            return services;
        }

        // Protocol. Lambda's ADOT sidecar speaks http/protobuf on 4318, so that stays the default.
        // Local dev takes whatever the endpoint actually speaks, because it varies: the Aspire
        // dashboard's OTLP endpoint is gRPC over HTTP/2 ONLY and answers an HTTP/1.1 POST with
        // "An HTTP/1.x request was sent to an HTTP/2 only endpoint" — every span and log record
        // silently dropped, dashboard stuck on "No traces found". Each AppHost already bridges
        // Aspire's OTEL_EXPORTER_OTLP_PROTOCOL onto Otel:Exporter:Protocol; this reads it.
        OtlpExportProtocol protocol = OtlpExportProtocol.HttpProtobuf;
        if (!isLambda && string.Equals(
                GetDirectExportProtocol(configuration), "grpc", StringComparison.OrdinalIgnoreCase))
        {
            protocol = OtlpExportProtocol.Grpc;
        }

        // gRPC names the signal in the request itself and takes the base URL unchanged; the HTTP
        // binding needs the per-signal path appended.
        string tracesEndpoint;
        string logsEndpoint;
        if (protocol == OtlpExportProtocol.Grpc)
        {
            tracesEndpoint = otlpBaseUrl;
            logsEndpoint = otlpBaseUrl;
        }
        else
        {
            string signalRoot = otlpBaseUrl.TrimEnd('/');
            tracesEndpoint = $"{signalRoot}/v1/traces";
            logsEndpoint = $"{signalRoot}/v1/logs";
        }

        // In Lambda the ADOT Collector handles auth via collector.yaml; only needed for local dev
        string authHeader;
        if (isLambda)
        {
            authHeader = string.Empty;
        }
        else
        {
            authHeader = GetDirectExportAuthHeader(configuration);
        }
        int exportTimeoutMs = isLambda ? 5_000 : 30_000;

        // Build the resource ONCE — applied to traces AND logs.
        ResourceBuilder resourceBuilder = ResourceBuilder.CreateDefault()
            .AddService(name, serviceVersion: version)
            .AddAttributes(new[]
            {
                new KeyValuePair<string, object>("deployment.environment", envName),
                new KeyValuePair<string, object>("service.instance.id", Environment.MachineName)
            });

        if (isLambda)
        {
            resourceBuilder.AddAttributes(new[]
            {
                new KeyValuePair<string, object>("faas.name",
                    Environment.GetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME") ?? "unknown"),
                new KeyValuePair<string, object>("cloud.provider", "aws"),
                new KeyValuePair<string, object>("cloud.platform", "aws_lambda"),
                new KeyValuePair<string, object>("cloud.region",
                    Environment.GetEnvironmentVariable("AWS_REGION") ?? "unknown")
            });
        }

        // ─── Tracing ───
        TracerProviderBuilder tracerBuilder = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(resourceBuilder)
            .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(
                configuration.GetValue("Otel:Tracing:SampleRate", 1.0))))
            .AddSource(Diagnostics.SourceName)
            .AddHttpClientInstrumentation(options =>
            {
                options.FilterHttpRequestMessage = request =>
                {
                    if (request.RequestUri == null)
                    {
                        return false;
                    }
                    Uri uri = request.RequestUri;
                    string host = uri.Host.ToLowerInvariant();

                    // Lambda runtime paths
                    if (uri.AbsolutePath.Contains("/runtime/invocation"))
                    {
                        return false;
                    }

                    // AWS SDK service calls
                    if (host.Contains("amazonaws.com"))
                    {
                        return false;
                    }

                    // ADOT Collector on localhost — don't trace our own telemetry exports
                    if (host == "localhost" && (uri.Port == 4317 || uri.Port == 4318))
                    {
                        return false;
                    }

                    return true;
                };
            });

        configureTracing?.Invoke(tracerBuilder);

        tracerBuilder.AddOtlpExporter(options =>
        {
            options.Endpoint = new Uri(tracesEndpoint);
            if (!string.IsNullOrEmpty(authHeader))
            {
                options.Headers = authHeader;
            }
            options.Protocol = protocol;
            options.TimeoutMilliseconds = exportTimeoutMs;
        });

        _tracerProvider = tracerBuilder.Build();

        // Register the provider in DI so annotation-based Lambdas can inject it for flush.
        services.AddSingleton(_tracerProvider);

        // Register ActivitySource for DI injection, so anything that starts custom spans can take
        // it as a dependency instead of reaching for the static. NOT needed by
        // RequestTelemetryMiddleware any more — that enriches Activity.Current and starts no span.
        services.AddSingleton(Diagnostics.ActivitySource);

        // ─── Flush on shutdown via IHostApplicationLifetime ───
        // For minimal hosting (AddAWSLambdaHosting) there is no FunctionHandlerAsync to
        // override, so this hosted service guarantees flush before the container freezes.
        // Classic (LambdaEntryPoint) shapes also benefit from this as a safety net.
        services.AddSingleton<IHostedService, OtelFlushService>();

        // ─── Logs ───
        // Logs are a SEPARATE provider that does NOT inherit the resource above.
        // Share the same resource explicitly or logs ship as `unknown_service`.
        logging.AddOpenTelemetry(o =>
        {
            o.SetResourceBuilder(resourceBuilder);
            o.IncludeFormattedMessage = true;
            o.IncludeScopes = true;
            o.ParseStateValues = true;

            o.AddOtlpExporter((exporterOptions, processorOptions) =>
            {
                exporterOptions.Endpoint = new Uri(logsEndpoint);

                if (!string.IsNullOrEmpty(authHeader))
                {
                    exporterOptions.Headers = authHeader;
                }

                exporterOptions.Protocol = protocol;
                exporterOptions.TimeoutMilliseconds = exportTimeoutMs;

                // In Lambda: batch is safe — exports go to localhost (~1ms), collector handles flush
                // In local dev: simple keeps logs synchronous for immediate visibility
                processorOptions.ExportProcessorType = isLambda
                    ? ExportProcessorType.Batch
                    : ExportProcessorType.Simple;
            });
        });

        return services;
    }

    /// <summary>
    /// Ensures TracerProvider and ActivitySource are registered in DI even when OTel
    /// is disabled — prevents [FromServices] injection failures.
    /// </summary>
    private static void EnsureProvidersRegistered(IServiceCollection services)
    {
        if (_tracerProvider is null)
        {
            _tracerProvider = Sdk.CreateTracerProviderBuilder().Build();
        }

        services.AddSingleton(_tracerProvider);
        services.AddSingleton(Diagnostics.ActivitySource);
    }

    /// <summary>
    /// Forces all buffered spans to be exported before Lambda freezes the container.
    /// Safe to call on every invocation — does not dispose the provider.
    /// <para>
    /// Classic (LambdaEntryPoint) shapes: call from <c>FunctionHandlerAsync</c> finally block.
    /// Minimal hosting shapes: called automatically by <see cref="OtelFlushService"/>.
    /// </para>
    /// <para>
    /// Logs are not flushed here: they run through the <c>ILoggerFactory</c>'s OTel provider,
    /// which the host disposes (and thereby flushes) on shutdown.
    /// </para>
    /// </summary>
    public static void FlushTelemetry(int timeoutMs = 10_000)
    {
        _tracerProvider?.ForceFlush(timeoutMs);
    }

    /// <summary>
    /// Reads the direct-export OTLP base URL for local dev from IConfiguration.
    /// Returns null if not configured — caller skips OTel gracefully.
    /// </summary>
    private static string? GetDirectExportBaseUrl(IConfiguration configuration)
    {
        return configuration["Otel:Exporter:Endpoint"];
    }

    /// <summary>
    /// Reads the direct-export auth header for local dev from IConfiguration.
    /// Returns empty if not configured (unauthenticated backend for dev).
    /// </summary>
    private static string GetDirectExportAuthHeader(IConfiguration configuration)
    {
        string? headers = configuration["Otel:Exporter:Headers"];
        return headers ?? string.Empty;
    }

    /// <summary>
    /// Reads the direct-export OTLP protocol for local dev — "grpc" or "http/protobuf", as the
    /// AppHost bridged it from Aspire. Returns null when unset, leaving the http/protobuf default.
    /// </summary>
    private static string? GetDirectExportProtocol(IConfiguration configuration)
    {
        return configuration["Otel:Exporter:Protocol"];
    }

    /// <summary>
    /// Hosted service that flushes OTel providers on application shutdown via
    /// <see cref="IHostApplicationLifetime.ApplicationStopping"/>.
    /// For minimal hosting (AddAWSLambdaHosting) this is the primary flush mechanism.
    /// For classic (LambdaEntryPoint) shapes it acts as a safety net.
    /// </summary>
    private sealed class OtelFlushService : IHostedService
    {
        private readonly IHostApplicationLifetime _lifetime;

        public OtelFlushService(IHostApplicationLifetime lifetime)
        {
            _lifetime = lifetime;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _lifetime.ApplicationStopping.Register(() => FlushTelemetry());
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
