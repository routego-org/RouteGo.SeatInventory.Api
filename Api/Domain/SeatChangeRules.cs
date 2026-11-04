namespace Api.Domain;

/// <summary>What a seat change may be: 1 to 6 moves, with no seat named twice across them.</summary>
public static class SeatChangeRules
{
    /// <summary>The most moves one seat change may make — a booking has at most 6 seats.</summary>
    public const int MaxMoves = SeatHoldRules.MaxSeats;

    /// <summary>True for 1 to 6 moves.</summary>
    public static bool IsValidMoveCount(int moveCount)
    {
        return moveCount >= 1 && moveCount <= MaxMoves;
    }

    /// <summary>True when no seat label appears twice among every from-seat and to-seat of the change.</summary>
    public static bool IsEverySeatNamedOnce(List<string> seatNos)
    {
        int distinct = seatNos.Distinct(StringComparer.Ordinal).Count();
        return distinct == seatNos.Count;
    }
}
