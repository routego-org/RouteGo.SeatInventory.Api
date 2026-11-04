namespace Api.Common.Behaviors;

/// <summary>
/// The correlation identifier of a request: the caller's own when it sends one, otherwise a new
/// one. It is stored with every seat the request changes, so the events those changes cause carry
/// it too.
/// </summary>
public static class CorrelationIds
{
    /// <summary>The request header a caller sends its correlation identifier in.</summary>
    public const string HeaderName = "X-Correlation-Id";

    // A caller-supplied identifier longer than this is replaced rather than stored on every seat.
    private const int MaxLength = 128;

    /// <summary>The caller's correlation identifier, or a new UUID when it sent none or one too long to keep.</summary>
    public static string Read(HttpRequest request)
    {
        string? supplied = request.Headers[HeaderName];
        if (string.IsNullOrWhiteSpace(supplied) || supplied.Length > MaxLength)
        {
            return Guid.NewGuid().ToString("D");
        }

        return supplied;
    }
}
