// Drop into <ProjectRoot>/Observability/Diagnostics.cs.
//
// ONE ActivitySource per project, named after the service, reused everywhere.
// Register it with the OpenTelemetry SDK so emitted spans are collected
// (see Observability/AddObservabilityExtensions.cs):
//
//   .AddSource(Diagnostics.SourceName)
//
// The ActivitySource is also registered in DI by AddObservability() so it can be
// injected into middleware (RequestTelemetryMiddleware) and annotation-based Lambda
// functions. For non-HTTP Lambdas, use the static field directly.
//
// Use ActivitySource.StartActivity for custom spans only where the built-in
// instrumentation (ASP.NET Core / HttpClient / AWS Lambda) is not granular enough.
// See references/custom-instrumentation.md.

using System.Diagnostics;

namespace Api.Observability;

/// <summary>
/// Project-wide <see cref="ActivitySource"/> for custom OpenTelemetry spans. It is
/// named after the service so the OpenTelemetry SDK picks it up when registered via
/// <c>AddSource(Diagnostics.SourceName)</c>.
/// </summary>
public static class Diagnostics
{
    /// <summary>The name used for the <see cref="ActivitySource"/>. Match the OTel service name.</summary>
    public const string SourceName = "Api";

    /// <summary>The shared <see cref="ActivitySource"/> for starting custom spans.</summary>
    public static readonly ActivitySource ActivitySource = new(SourceName);
}
