namespace Api.Common.Behaviors;

/// <summary>The role values Identity writes into the role claim, as endpoints name them in [Authorize].</summary>
public static class AccessRoles
{
    /// <summary>RouteGo staff; may act on any trip.</summary>
    public const string Admin = "Admin";

    /// <summary>A bus operator; may act only on its own trips.</summary>
    public const string Operator = "Operator";

    /// <summary>Staff views open to both: an admin for any trip, an operator for its own.</summary>
    public const string AdminOrOperator = Admin + "," + Operator;
}
