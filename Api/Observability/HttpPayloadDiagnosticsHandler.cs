// Drop into <ProjectRoot>/Observability/HttpPayloadDiagnosticsHandler.cs.
//
// DEVELOPMENT ONLY. Registered from Program.cs behind IsDevelopment(), and it re-checks the
// environment itself so a later global registration cannot leak payloads into a deployed run.
//
// OpenTelemetry's HttpClient instrumentation records the request LINE and the response STATUS —
// method, url, status code, duration — and never the bodies. That is deliberate: a payload is
// unbounded and routinely carries credentials, so the semantic conventions leave it out. The
// consequence is that a failing call shows up in the dashboard as "HTTP POST 403" with nothing
// to say WHY, which is exactly when the body is the only thing worth reading.
//
// This handler puts the bodies back, as a span EVENT rather than span tags: one request can make
// several backend calls, and events stack where tags would overwrite each other.

using System.Diagnostics;

namespace Api.Observability;

/// <summary>
/// Attaches outbound request/response bodies to the current span as an <c>http.payload</c> event,
/// so a failing call can be diagnosed from the trace instead of a re-run with a breakpoint.
/// Development only.
/// </summary>
public sealed class HttpPayloadDiagnosticsHandler : DelegatingHandler
{
    /// <summary>Bodies larger than this are skipped rather than truncated — a partial JSON
    /// document reads as corrupt data and sends the reader chasing the wrong fault.</summary>
    private const int MaxBodyBytes = 32 * 1024;

    /// <summary>
    /// Successful responses are NOT captured by default: a 2xx body is where a credential-issuing
    /// call (a token exchange, a sign-in) returns the credential itself. Failures carry the
    /// diagnosis and no secret, so those are always captured. Flip this to true for a session that
    /// needs to see a successful payload — and flip it back.
    /// </summary>
    private const bool CaptureSuccessfulResponses = false;

    private static readonly string[] _readableMediaTypes =
        { "application/json", "application/problem+json", "text/plain", "text/html", "application/xml", "text/xml" };

    private readonly IHostEnvironment _environment;

    public HttpPayloadDiagnosticsHandler(IHostEnvironment environment)
    {
        _environment = environment;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!_environment.IsDevelopment())
        {
            return await base.SendAsync(request, cancellationToken);
        }

        // Read BEFORE sending: HttpContent buffers what it reads, so the send below still sees the
        // body, but reading afterwards would race a content the transport may already have disposed.
        string? requestBody = await ReadBodyAsync(request.Content, cancellationToken);

        HttpResponseMessage response = await base.SendAsync(request, cancellationToken);

        // Activity.Current here is the ENCLOSING span — the incoming request — because the
        // "HTTP POST" client span starts and ends deeper in the pipeline, below this handler.
        // The event therefore lands on the parent of the red span in the dashboard, one row up,
        // in the same trace.
        Activity? activity = Activity.Current;
        if (activity is null || (response.IsSuccessStatusCode && !CaptureSuccessfulResponses))
        {
            return response;
        }

        string? responseBody = await ReadBodyAsync(response.Content, cancellationToken);

        activity.AddEvent(new ActivityEvent("http.payload", tags: new ActivityTagsCollection
        {
            { "http.request.method", request.Method.Method },
            { "url.full", request.RequestUri?.ToString() ?? "(none)" },
            { "http.response.status_code", (int)response.StatusCode },
            { "http.request.body", requestBody ?? "(not captured)" },
            { "http.response.body", responseBody ?? "(not captured)" }
        }));

        return response;
    }

    /// <summary>
    /// Returns the body as text when it is safe to read one: a text-shaped media type, of known
    /// and modest length. Anything else — a stream upload, a file download, an unknown length —
    /// returns null rather than draining a content the caller still needs.
    /// </summary>
    private static async Task<string?> ReadBodyAsync(HttpContent? content, CancellationToken cancellationToken)
    {
        if (content is null)
        {
            return null;
        }

        string? mediaType = content.Headers.ContentType?.MediaType;
        if (mediaType is null || !_readableMediaTypes.Contains(mediaType, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        long? length = content.Headers.ContentLength;
        if (length > MaxBodyBytes)
        {
            return null;
        }

        try
        {
            // Bounds an unknown-length body too: LoadIntoBufferAsync throws once it passes the cap,
            // and buffering is what leaves the content re-readable for whoever asked for it.
            await content.LoadIntoBufferAsync(MaxBodyBytes, cancellationToken);
            return await content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or IOException)
        {
            // Diagnostics must never be the reason a call fails.
            return null;
        }
    }
}
