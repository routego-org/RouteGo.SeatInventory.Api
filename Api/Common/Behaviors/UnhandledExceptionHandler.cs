using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;

namespace Api.Common.Behaviors;

/// <summary>
/// Turns unhandled exceptions into a 500 application/problem+json response (RFC 7807).
/// Registered via AddExceptionHandler&lt;&gt; + app.UseExceptionHandler() in Program.cs
/// (references/host-components.md).
/// </summary>
public sealed class UnhandledExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _problems;

    public UnhandledExceptionHandler(IProblemDetailsService problems)
    {
        _problems = problems;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;

        // Put the exception on the request's span — type, message and stack trace — because this
        // handler is the LAST thing in the pipeline that still holds it. Returning true below
        // stops UseExceptionHandler rethrowing, so the exception never reaches the host and
        // AddAspNetCoreInstrumentation's RecordException never fires: without this line a 500 is
        // a span marked Error with nothing to say why, and nothing in the logs either. The span
        // is the right home for it — it carries the traceId the response hands the caller.
        Activity.Current?.AddException(exception);

        // Carries the catalog's INTERNAL_ERROR code like every other problem response: a client
        // switching on `code` must not have to special-case the one status it cannot predict.
        // The detail is deliberately absent — an exception message can leak SQL, paths, and
        // dependency names; traceId is how a caller reports this to us.
        return await _problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails =
            {
                Title = "An unexpected error occurred.",
                Status = StatusCodes.Status500InternalServerError,
                Type = ProblemTypes.InternalError.Type,
                Extensions = { ["code"] = ProblemTypes.InternalError.Code },
            },
        });
    }
}
