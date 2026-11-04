// Dev-only orchestrator. `dotnet run` here starts Api plus the Aspire dashboard, whose
// built-in OTLP receiver becomes the local telemetry backend — live traces and logs
// per request, no code change in the app, no vendor, still OTLP. Without it, local telemetry is
// simply off (AddObservability no-ops when Otel:Exporter:Endpoint is unset — it never crashes).
//
// One-way dependency: this AppHost references Api; Api (and its deploy/publish
// pipeline) NEVER references this. Never add Aspire packages to Api itself — that would
// double-register exporters and ship Aspire in the deployed artifact. (check_setup.py RED-flags
// Aspire packages found in the app project.)
//
// See skills-aspnetcore-project-setup/references/aspire-local-dev.md for the telemetry contract —
// and, if one AppHost ever runs several projects and their interleaved per-request logging makes
// the console unreadable, the opt-in "Quieting the console" recipe in the same document.

IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.Api>("api")
    .WithEnvironment(ctx =>
    {
        // Bridge Aspire's injected OTLP endpoint/headers/protocol onto the Otel:Exporter:* keys
        // AddObservability reads via IConfiguration. The env-var provider maps __ to : — so the
        // dashboard's per-run endpoint + rotating api key reach the app with zero app change.
        if (ctx.EnvironmentVariables.TryGetValue("OTEL_EXPORTER_OTLP_ENDPOINT", out object? endpoint))
        {
            ctx.EnvironmentVariables["Otel__Exporter__Endpoint"] = endpoint;
        }

        ctx.EnvironmentVariables["Otel__Exporter__Headers"] =
            ctx.EnvironmentVariables.TryGetValue("OTEL_EXPORTER_OTLP_HEADERS", out object? headers)
                ? headers
                : "";

        if (ctx.EnvironmentVariables.TryGetValue("OTEL_EXPORTER_OTLP_PROTOCOL", out object? protocol))
        {
            ctx.EnvironmentVariables["Otel__Exporter__Protocol"] = protocol;
        }
    });

builder.Build().Run();
