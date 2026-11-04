namespace Api.Domain;

/// <summary>What an operator's block or unblock may name: 1 to 60 different seats of one trip.</summary>
public static class SeatBlockRules
{
    /// <summary>The most seats one block or unblock may name.</summary>
    public const int MaxSeats = 60;

    /// <summary>True for 1 to 60 different, non-blank seat labels.</summary>
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
}
