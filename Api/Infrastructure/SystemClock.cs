namespace Api.Infrastructure;

/// <summary>Real system clock. Immutable and thread-safe (safe as a fleet-shared singleton).</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow
    {
        get
        {
            // The coding standard bans DateTimeOffset.UtcNow everywhere else (rule K5: inject
            // IClock). This adapter IS the clock — the one sanctioned call site behind the port.
#pragma warning disable RS0030
            return DateTimeOffset.UtcNow;
#pragma warning restore RS0030
        }
    }
}
