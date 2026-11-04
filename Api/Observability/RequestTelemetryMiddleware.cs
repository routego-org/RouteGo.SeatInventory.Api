// Drop into <ProjectRoot>/Observability/RequestTelemetryMiddleware.cs.
// Web stacks only (Web API, Razor Pages UI). The Lambda worker has no HTTP pipeline — each
// [LambdaFunction] opens its own root span in the handler instead.
//
// This middleware ENRICHES the request's existing server span. It does NOT create one, and it
// does NOT extract trace context: AddAspNetCoreInstrumentation() already does both, and a second
// server span is reported as a SECOND TRANSACTION for the same request — see "Why this starts no
// span" below before adding one back.
//
// It:
//  - Stamps user.id / tenant.id from the authenticated principal onto the request's span.
//  - Logs every request with its status and elapsed time.
//
// ─── Why this starts no span, and extracts no trace context ───────────────────────────
//
// Both jobs are already done, by AddAspNetCoreInstrumentation() in Program.cs:
//
//  - It starts the server span and names it "{method} {http.route}" — the route TEMPLATE
//    ("POST pois"), which groups /pois/1 and /pois/2 into one transaction. A span named from
//    Request.Path would instead read "POST /pois" and split every id into its own.
//  - It extracts traceparent / tracestate / baggage with the propagator AddObservability()
//    configures, so the span already participates in the caller's trace and Baggage.Current is
//    already restored.
//
// An earlier version of this file did both a second time:
//
//     StartActivity($"{method} {path}", ActivityKind.Server, parentContext.ActivityContext)
//
// That is two defects. The span itself is one: a backend reports an ActivityKind.Server span as a
// transaction, so every route appeared TWICE with identical throughput — "POST pois" from the
// instrumentation and "POST /pois" from here. The leading slash was the tell.
//
// The explicit parent context is the other. It OVERRIDES the ambient Activity.Current, so with an
// inbound traceparent the span parented to the REMOTE caller and became a SIBLING of the request
// span rather than a child — every custom, database and HttpClient span then nested under the
// duplicate, and the real request span looked childless. (With no inbound traceparent the
// extracted context is default and the runtime falls back to Activity.Current, so it nested as a
// child: still a second transaction, just a less obvious one.)
//
// So: enrich Activity.Current, and never start a Server span in middleware. A span for work of
// your own is fine — start it WITHOUT a parent argument so it nests properly:
//
//     using Activity? activity = Diagnostics.ActivitySource.StartActivity("Pois.Curate");
//
// Register FIRST in the pipeline so it wraps all other middleware (the api's shape shown; a UI
// wires Razor Pages and fills its own exception-handler seam):
//
//   app.UseMiddleware<RequestTelemetryMiddleware>();   // outermost
//   app.UseExceptionHandler();
//   app.UseAuthentication();
//   app.UseAuthorization();
//   app.MapControllers();

using System.Diagnostics;
using System.Security.Claims;

namespace Api.Observability;

/// <summary>
/// Enriches the request's server span with user/tenant identity, and logs every request with its
/// result status and elapsed time.
/// <para>
/// It deliberately starts <b>no span of its own</b>. <c>AddAspNetCoreInstrumentation()</c> already
/// starts one per request and already extracts the upstream trace context; a second server span
/// here is reported as a second transaction for the same request. The file header records how that
/// failed in practice.
/// </para>
/// </summary>
public sealed class RequestTelemetryMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestTelemetryMiddleware> _logger;

    /// <summary>Creates the middleware.</summary>
    public RequestTelemetryMiddleware(
        RequestDelegate next,
        ILogger<RequestTelemetryMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <summary>Runs the middleware for the current request.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew(); // var: the type is apparent from the factory (F14)
        string method = context.Request.Method;
        string path = context.Request.Path.Value ?? "/";
        string? queryString = context.Request.QueryString.HasValue
            ? context.Request.QueryString.Value
            : null;

        // The request's server span, started by AddAspNetCoreInstrumentation before any middleware
        // runs. Null only when tracing is disabled or the request was filtered out (the /health
        // filter, for instance), so every use below is null-conditional.
        //
        // NOT re-tagged with method, path, query or status: the instrumentation sets
        // http.request.method, url.path, url.query, http.route and http.response.status_code
        // itself, to the semantic conventions. Setting them again would either write the same
        // value twice or, worse, disagree with it.
        Activity? activity = Activity.Current;

        string traceId = activity?.TraceId.ToString() ?? "no-trace";

        _logger.LogInformation(
            "[OTel] {Method} {Path}{Query} | TraceId={TraceId}",
            method, path, queryString, traceId);

        // SPAN STATUS IS NOT SET HERE, deliberately. The instrumentation owns the span and sets
        // Error for 5xx itself; writing it again from here can only duplicate or contradict it —
        // and an earlier version did contradict it, marking 4xx as Error where the instrumentation
        // leaves it Unset. A 404 is not a server fault, so that inflated the backend's
        // failed-transaction rate for every client mistake. The log levels below still separate
        // 4xx from 5xx, which is where that distinction belongs.
        //
        // NOR IS THE EXCEPTION RECORDED HERE, and which of two reasons applies depends on whether
        // this app has an exception handler in the pipeline. Both end in the same place:
        //
        //  - HANDLED (the api always: Program.cs calls UseExceptionHandler right after this
        //    middleware; a UI once the slice skill fills its seam with UseExceptionHandler("/error")).
        //    The handler returns "handled", so nothing rethrows and the catch below never runs —
        //    the request returns 500 through the normal path above. Recording the exception is that
        //    handler's job, being the last thing still holding it: the api's
        //    UnhandledExceptionHandler calls Activity.AddException.
        //
        //  - UNHANDLED (a freshly stamped UI, before its seam is filled). The exception escapes to
        //    the host, where AddAspNetCoreInstrumentation records it as a proper exception event
        //    because Program.cs sets RecordException = true.
        //
        // Either way the span ends up with the exception, recorded once, as an EVENT — which is how
        // the semantic conventions model it, and what a backend reads. Flat exception.type /
        // exception.message tags set from here would be neither.
        try
        {
            await _next(context);

            stopwatch.Stop();
            int statusCode = context.Response.StatusCode;

            // Identity, which nothing else can supply: it is only knowable after the
            // authentication middleware inside this pipeline has run.
            EnrichWithIdentity(activity, context);

            if (statusCode >= 500)
            {
                _logger.LogError(
                    "[OTel] {Method} {Path} → {StatusCode} in {ElapsedMs}ms | TraceId={TraceId}",
                    method, path, statusCode, stopwatch.ElapsedMilliseconds, traceId);
            }
            else if (statusCode >= 400)
            {
                _logger.LogWarning(
                    "[OTel] {Method} {Path} → {StatusCode} in {ElapsedMs}ms | TraceId={TraceId}",
                    method, path, statusCode, stopwatch.ElapsedMilliseconds, traceId);
            }
            else
            {
                _logger.LogInformation(
                    "[OTel] {Method} {Path} → {StatusCode} in {ElapsedMs}ms | TraceId={TraceId}",
                    method, path, statusCode, stopwatch.ElapsedMilliseconds, traceId);
            }
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            // Identity before the rethrow, so a failed request is still attributable. Reached only
            // when nothing downstream handled the exception — so never on the api, whose
            // UseExceptionHandler sits inside this middleware and writes a problem response instead
            // of rethrowing, and on a UI only until its exception-handler seam is filled.
            EnrichWithIdentity(activity, context);

            _logger.LogError(ex,
                "[OTel] {Method} {Path} → EXCEPTION in {ElapsedMs}ms | TraceId={TraceId} | {ExceptionType}: {ExceptionMessage}",
                method, path, stopwatch.ElapsedMilliseconds, traceId,
                ex.GetType().Name, ex.Message);

            throw;
        }
    }

    /// <summary>
    /// Stamps the authenticated user (and tenant, if available) onto the request's span, so a
    /// trace can be searched by who made it.
    /// <para>
    /// These land on the <b>span</b> only. A log line written inside the request carries the trace
    /// and span ids — which is what joins it to this span in the backend — but not the span's
    /// tags: setting a tag does not add it to the logger's scope. To search logs by user directly,
    /// add an <c>ILogger.BeginScope</c> here as well, or pivot from the log to its trace and read
    /// the tag there.
    /// </para>
    /// </summary>
    private static void EnrichWithIdentity(Activity? activity, HttpContext context)
    {
        if (activity is null || context.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        string? userId = context.User.FindFirst("sub")?.Value;
        if (userId is null)
        {
            userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        }
        if (!string.IsNullOrEmpty(userId))
        {
            activity.SetTag("user.id", userId);
        }

        // If the project has a tenant claim, stamp it too. Adjust the claim type
        // to match the project's auth setup, or inject an ITenantContext here.
        string? tenantId = context.User.FindFirst("tenant_id")?.Value;
        if (!string.IsNullOrEmpty(tenantId))
        {
            activity.SetTag("tenant.id", tenantId);
        }
    }
}
