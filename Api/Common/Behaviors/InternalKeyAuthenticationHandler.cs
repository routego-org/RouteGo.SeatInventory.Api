using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Api.Common.Behaviors;

/// <summary>
/// Accepts a call that presents the RouteGo internal key in the X-Internal-Key header, and answers
/// a call without it, or with a wrong one, with a 401 UNAUTHORIZED problem. The caller is never a
/// person: a successful call is known only as "a RouteGo service holding the key". Endpoints that
/// name this scheme are checked by it alone, so an access token is never accepted in its place.
/// </summary>
public sealed class InternalKeyAuthenticationHandler : AuthenticationHandler<InternalKeyAuthenticationOptions>
{
    private const string WrongKeyMessage = "The internal key presented is wrong.";

    public InternalKeyAuthenticationHandler(
        IOptionsMonitor<InternalKeyAuthenticationOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? presentedKey = Request.Headers[InternalKeyAuthenticationOptions.HeaderName];
        if (string.IsNullOrEmpty(presentedKey))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!IsSameKey(presentedKey, Options.Key))
        {
            return Task.FromResult(AuthenticateResult.Fail(WrongKeyMessage));
        }

        var identity = new ClaimsIdentity(Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    /// <inheritdoc />
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        await AuthenticationProblems.WriteInternalKeyUnauthorizedAsync(Context);
    }

    // Both keys are hashed first so the comparison takes the same time whatever the presented
    // key's length or how much of it is right, and reveals neither to a caller timing it.
    private static bool IsSameKey(string presentedKey, string expectedKey)
    {
        byte[] presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presentedKey));
        byte[] expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expectedKey));
        return CryptographicOperations.FixedTimeEquals(presentedHash, expectedHash);
    }
}
