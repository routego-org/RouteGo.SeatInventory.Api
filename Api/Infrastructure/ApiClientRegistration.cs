namespace Api.Infrastructure;

/// <summary>
/// The one way this app registers an outbound API client. The base address comes from configuration
/// with its trailing slash normalised, so a versioned base URL cannot silently lose its last segment
/// (references/configuration.md).
/// Authentication is deliberately NOT wired here: this skill owns the host, not any particular
/// auth scheme. The method returns the IHttpClientBuilder so the caller chains whatever that API
/// needs — an OAuth handler for a gateway-fronted service, nothing at all for a public one.
/// </summary>
public static class ApiClientRegistration
{
    /// <summary>
    /// Registers the named client for one API and returns its builder for further configuration.
    /// Name it from HttpClients and key it from ConfigKeys, so both ends stay spelled once.
    /// </summary>
    public static IHttpClientBuilder AddApiClient(
        this IServiceCollection services,
        IConfiguration configuration,
        string name,
        string baseUrlKey)
    {
        return services.AddHttpClient(name, client =>
        {
            client.BaseAddress = configuration.RequireBaseAddress(baseUrlKey);
        });
    }
}
