using Amazon.SecretsManager;
using Amazon.SecretsManager.Extensions.Caching;

namespace Api.Infrastructure;

// Standard AWS Secrets Manager access for this project
// (skills-aspnetcore-project-setup/references/secrets.md). Registered once as a singleton in
// Program.cs. ONE lookup key — the secret's own NAME — gated by environment:
//   Development      -> Configuration[secretName] ONLY (user-secrets on a laptop, injected config in
//                       tests). AWS is never called: no credentials or network needed, and a missing
//                       key fails fast instead of silently reaching a cloud database from a dev box.
//   everything else  -> AWS Secrets Manager ONLY, via the execution role, through SecretsManagerCache
//                       (thread-safe, refreshes on access — picks up a rotated secret without a
//                       redeploy). Configuration is ignored, so nothing injected into a deployed
//                       environment can redirect a secret.
public sealed class SecretResolver
{
    private readonly IConfiguration _config;
    private readonly IHostEnvironment _environment;
    private readonly Lazy<SecretsManagerCache> _cache;

    public SecretResolver(IConfiguration config, IHostEnvironment environment)
    {
        _config = config;
        _environment = environment;
        // Lazy so Development never constructs an AWS client at all.
        _cache = new Lazy<SecretsManagerCache>(() =>
            new SecretsManagerCache(config.GetAWSOptions().CreateServiceClient<IAmazonSecretsManager>()));
    }

    // Resolve a secret VALUE by its name — the same name in every environment; only the source
    // differs. Returns null when the source has no value (caller decides if that is fatal).
    public async Task<string?> ResolveAsync(string secretName)
    {
        if (_environment.IsDevelopment())
        {
            return _config[secretName];
        }

        return await _cache.Value.GetSecretString(secretName);
    }
}
