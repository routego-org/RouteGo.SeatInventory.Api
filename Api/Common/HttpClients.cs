namespace Api.Common;

/// <summary>
/// The name of every named HttpClient this app registers — one entry per API it calls outbound.
/// Consumers pass one of these to IHttpClientFactory.CreateClient; the composition root registers
/// each one against its base-URL key in ConfigKeys through AddApiClient. Adding an API adds a line
/// here, not another file (references/configuration.md).
/// </summary>
public static class HttpClients
{
    // ══════════════════════════════════════════════════════════════════════════
    // FILLED BY the sibling slice skill and by feature work: one const per API
    // this app calls outbound.
    //
    //   public const string Backend = "Backend";
    //
    // Register it in the composition root, naming its base URL from ConfigKeys
    // and chaining whatever handler that API needs:
    //
    //   builder.Services.AddApiClient(builder.Configuration,
    //           HttpClients.Backend, ConfigKeys.BackendBaseUrl)
    //       .AddHttpMessageHandler<SomeAuthHandler>();
    //
    // Never assign a client's BaseAddress by hand — AddApiClient normalises the
    // trailing slash a versioned base URL depends on, and check_setup.py fails a
    // project that sets one anywhere else.
    // ══════════════════════════════════════════════════════════════════════════
}
