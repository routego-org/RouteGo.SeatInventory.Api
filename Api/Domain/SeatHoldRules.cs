namespace Api.Domain;

/// <summary>What a hold may be: 1 to 6 different seats of one trip, for 1 second up to the hold ceiling.</summary>
public static class SeatHoldRules
{
    /// <summary>The most seats one booking may hold on one trip.</summary>
    public const int MaxSeats = 6;

    /// <summary>How long a hold lasts when Booking does not say.</summary>
    public const int DefaultLengthSeconds = 600;

    /// <summary>
    /// The default hold ceiling and its maximum. Each hold's timer is one queue message delayed until
    /// the hold expiry, and the queue cannot delay a message longer than this, so the ceiling may be
    /// lowered by configuration but never raised.
    /// </summary>
    public const int MaxCeilingSeconds = 900;

    /// <summary>The configured hold ceiling, 900 when none is set, or null when it is not 1 to 900.</summary>
    public static int? ToCeilingSeconds(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return MaxCeilingSeconds;
        }

        bool isNumber = int.TryParse(
            configured, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int seconds);
        if (!isNumber || seconds < 1 || seconds > MaxCeilingSeconds)
        {
            return null;
        }

        return seconds;
    }

    /// <summary>True for 1 to 6 different, non-blank seat labels.</summary>
    public static bool IsValidSeatList(List<string>? seatNos)
    {
        if (seatNos is null || seatNos.Count == 0 || seatNos.Count > MaxSeats)
        {
            return false;
        }

        if (seatNos.Exists(string.IsNullOrWhiteSpace))
        {
            return false;
        }

        int distinct = seatNos.Distinct(StringComparer.Ordinal).Count();
        return distinct == seatNos.Count;
    }

    /// <summary>True when no length is given, or it is at least one second; the ceiling is checked separately.</summary>
    public static bool IsValidLength(int? holdSeconds)
    {
        if (holdSeconds is null)
        {
            return true;
        }

        return holdSeconds.Value >= 1;
    }
}
