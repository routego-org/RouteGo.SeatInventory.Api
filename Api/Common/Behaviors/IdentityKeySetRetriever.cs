using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Api.Common.Behaviors;

/// <summary>
/// Reads Identity's published key set (/.well-known/jwks.json) into the configuration the JWT
/// bearer handler validates signatures against. Identity publishes the bare key set rather than
/// an OpenID discovery document, so the framework's discovery retriever cannot be used; the
/// framework's ConfigurationManager still does the caching and the immediate reload when a token
/// names a key ID the cached set does not hold.
/// </summary>
public sealed class IdentityKeySetRetriever : IConfigurationRetriever<OpenIdConnectConfiguration>
{
    /// <summary>Downloads the key set at <paramref name="address"/> and exposes its signing keys.</summary>
    /// <param name="address">The key set's address.</param>
    /// <param name="retriever">The framework's document retriever, which owns the HTTP call.</param>
    /// <param name="cancel">Cancels the download.</param>
    /// <returns>A configuration holding only the signing keys.</returns>
    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(
        string address, IDocumentRetriever retriever, CancellationToken cancel)
    {
        string document = await retriever.GetDocumentAsync(address, cancel);
        var keySet = new JsonWebKeySet(document);
        var configuration = new OpenIdConnectConfiguration();
        foreach (SecurityKey key in keySet.GetSigningKeys())
        {
            configuration.SigningKeys.Add(key);
        }

        return configuration;
    }
}
