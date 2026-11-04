namespace Api.Common.Ports;

// The one time seam — inject IClock and read UtcNow through it instead of calling
// DateTimeOffset.UtcNow directly, so time is substitutable in tests (references/host-components.md).
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
