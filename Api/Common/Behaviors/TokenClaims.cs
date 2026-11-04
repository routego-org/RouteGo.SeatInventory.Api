namespace Api.Common.Behaviors;

/// <summary>The claim names RouteGo.Identity.Api writes into every access token, kept as issued.</summary>
public static class TokenClaims
{
    /// <summary>The signed-in account's user identifier; recorded as who made a staff change.</summary>
    public const string Subject = "sub";

    /// <summary>The signed-in account's email.</summary>
    public const string Email = "email";

    /// <summary>Operator, Admin or Customer.</summary>
    public const string Role = "role";

    /// <summary>The operator's identifier; present only on Operator tokens. Owns every trip Fleet scheduled for it.</summary>
    public const string OperatorId = "operatorId";
}
