using System.Diagnostics;

namespace Api.Common.Behaviors;

/// <summary>
/// Stamps the standard response-metadata headers on every response
/// (references/host-components.md). Response.OnStarting is the latest safe point to set
/// headers; reuses the IClock port so tests can control the reported time.
/// </summary>
public sealed class ResponseMetadataMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IClock _clock;

    public ResponseMetadataMiddleware(RequestDelegate next, IClock clock)
    {
        _next = next;
        _clock = clock;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        long start = Stopwatch.GetTimestamp();
        context.Response.OnStarting(() =>
        {
            IHeaderDictionary headers = context.Response.Headers;
            string? traceId = Activity.Current?.TraceId.ToString();
            if (traceId is null)
            {
                traceId = context.TraceIdentifier;
            }
            headers["X-Trace-Id"] = traceId;
            headers["X-Server-Time-Epoch"] = _clock.UtcNow.ToUnixTimeSeconds().ToString();
            headers["Server-Timing"] = $"app;dur={Stopwatch.GetElapsedTime(start).TotalMilliseconds:F1}";
            return Task.CompletedTask;
        });
        await _next(context);
    }
}
