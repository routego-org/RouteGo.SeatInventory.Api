namespace Api.Features.TripSeats.DeleteSeatBlock;

/// <summary>
/// Access tokens shaped as RouteGo.Identity.Api issues them, signed with a key only the test host
/// trusts.
/// </summary>
public static class DeleteSeatBlockTokens
{
    /// <summary>The issuer every slice's test class sets identically (see GetSeatMap's fixture).</summary>
    public const string Issuer = "http://localhost:5001";

    /// <summary>The audience every slice's test class sets identically.</summary>
    public const string Audience = "routego";

    private static readonly IClock _realClock = new Infrastructure.SystemClock();
    private static readonly RsaSecurityKey _trustedKey = CreateKey("seat-inventory-tests-trusted");

    /// <summary>A valid token for an Admin or a Customer.</summary>
    public static string TokenFor(string role)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = Guid.NewGuid().ToString(),
            ["email"] = "person@example.com",
            ["role"] = role,
        };
        return Sign(claims);
    }

    /// <summary>A valid token for a given user of a given operator.</summary>
    public static string OperatorToken(Guid operatorId, Guid userId)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = userId.ToString(),
            ["email"] = "owner@busco.in",
            ["role"] = "Operator",
            ["operatorId"] = operatorId.ToString(),
        };
        return Sign(claims);
    }

    /// <summary>Makes the host trust the test signing key instead of fetching Identity's keys.</summary>
    public static void TrustTestSigningKey(Microsoft.Extensions.DependencyInjection.IServiceCollection services)
    {
        var configuration = new OpenIdConnectConfiguration();
        configuration.SigningKeys.Add(_trustedKey);
        Microsoft.Extensions.DependencyInjection.OptionsServiceCollectionExtensions.PostConfigure<JwtBearerOptions>(
            services,
            JwtBearerDefaults.AuthenticationScheme,
            options => options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration));
    }

    private static string Sign(Dictionary<string, object> claims)
    {
        // The host checks expiry against real time with no grace, so tokens are minted against it too.
        DateTime now = _realClock.UtcNow.UtcDateTime;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(15),
            Claims = claims,
            SigningCredentials = new SigningCredentials(_trustedKey, SecurityAlgorithms.RsaSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private static RsaSecurityKey CreateKey(string keyId)
    {
        var rsa = RSA.Create(2048);
        return new RsaSecurityKey(rsa) { KeyId = keyId };
    }
}
