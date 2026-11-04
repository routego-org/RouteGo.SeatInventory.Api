namespace Api.Infrastructure;

/// <summary>
/// Startup validation for environment-supplied configuration. Configuration reaches this app
/// through one channel in every environment — environment variables named by <see cref="ConfigKeys"/>
/// — so a key forgotten in the deployment template used to surface as a failed request on whichever
/// screen happened to need it first. These checks run at startup instead, naming every missing key
/// in a single message, so a bad deployment fails at cold start rather than in front of a user
/// (references/configuration.md).
/// </summary>
public static class ConfigurationValidation
{
    /// <summary>Throws when any of the keys is missing or blank, listing all of them at once.</summary>
    public static void RequireKeys(this IConfiguration configuration, params string[] keys)
    {
        string[] missing = keys.Where(key => string.IsNullOrWhiteSpace(configuration[key])).ToArray();
        if (missing.Length == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Missing required configuration: {string.Join(", ", missing)}. Each is supplied as an "
            + "environment variable of the same name — locally from LocalEntryPoints, deployed from "
            + "the deployment template.");
    }

    /// <summary>
    /// Returns a base address safe to assign to an HttpClient. A base URL carrying a path
    /// ("…/orders/v1") MUST end in '/': without it, URI resolution treats the last segment as a
    /// file and DROPS it when a relative request path is appended, quietly calling "…/entities"
    /// instead of "…/orders/v1/entities". Normalising here means no deployed value can reintroduce it.
    /// </summary>
    public static Uri RequireBaseAddress(this IConfiguration configuration, string key)
    {
        string? value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Missing required configuration: {key}.");
        }

        string normalized = value;
        if (!normalized.EndsWith('/'))
        {
            normalized += "/";
        }

        return new Uri(normalized);
    }
}
