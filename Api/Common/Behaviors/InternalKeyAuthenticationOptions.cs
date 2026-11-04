using Microsoft.AspNetCore.Authentication;

namespace Api.Common.Behaviors;

/// <summary>Settings of the internal-key authentication scheme: the key every Booking call must present.</summary>
public sealed class InternalKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    /// <summary>The name the scheme is registered under; Booking's endpoints authorize against it alone.</summary>
    public const string SchemeName = "InternalKey";

    /// <summary>
    /// The secret holding the key, by its one name in every environment: Development reads it from
    /// user-secrets, every other environment from Secrets Manager. The value is the key itself, as
    /// plain text. Not under a Seat Inventory path because the key is shared: Booking reads the same
    /// secret to call these operations.
    /// </summary>
    public const string SecretId = "routego/internal-key";

    /// <summary>The request header that carries the key, as the contract's security scheme names it.</summary>
    public const string HeaderName = "X-Internal-Key";

    /// <summary>The expected key, resolved from the secret store at startup.</summary>
    public string Key { get; set; } = string.Empty;
}
