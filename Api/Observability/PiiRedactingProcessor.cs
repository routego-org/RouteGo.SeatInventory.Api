// Drop into <ProjectRoot>/Observability/PiiRedactingProcessor.cs.
//
// ONE place that decides what gets redacted from spans — no per-call-site reasoning.
// Register on the tracer provider:
//
//   .WithTracing(t => t.AddProcessor<PiiRedactingProcessor>() ...)
//
// Runs on every span as it ends, before export.

using System.Diagnostics;
using System.Text.RegularExpressions;
using OpenTelemetry;

namespace Api.Observability;

/// <summary>
/// Redacts sensitive values from span tags at the exporter boundary so PII never
/// reaches the observability backend. The single source of truth for redaction.
/// </summary>
public sealed partial class PiiRedactingProcessor : BaseProcessor<Activity>
{
    // Tags that commonly carry PII. Add project-specific tag keys as needed.
    private static readonly string[] _sensitiveTags =
        { "http.url", "url.full", "http.target", "db.statement", "user.email" };

    /// <inheritdoc />
    public override void OnEnd(Activity activity)
    {
        foreach (string tag in _sensitiveTags)
        {
            if (activity.GetTagItem(tag) is string value && ContainsPii(value))
            {
                activity.SetTag(tag, Redact(value));
            }
        }
    }

    private static bool ContainsPii(string value)
    {
        return EmailRegex().IsMatch(value) || CardRegex().IsMatch(value);
    }

    private static string Redact(string value)
    {
        value = EmailRegex().Replace(value, "[redacted-email]");
        value = CardRegex().Replace(value, "[redacted-card]");
        return value;
    }

    [GeneratedRegex(@"[\w.+-]+@[\w-]+\.[\w.-]+", RegexOptions.Compiled)]
    private static partial Regex EmailRegex();

    // 13–16 digit runs, optionally separated by spaces/dashes — catches most card numbers.
    [GeneratedRegex(@"\b(?:\d[ -]*?){13,16}\b", RegexOptions.Compiled)]
    private static partial Regex CardRegex();
}
