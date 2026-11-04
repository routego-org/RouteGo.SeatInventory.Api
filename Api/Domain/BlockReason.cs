namespace Api.Domain;

/// <summary>Why a blocked seat is blocked. Only an operator block can ever be lifted.</summary>
public enum BlockReason
{
    Operator,
    TripCancelled,
}
