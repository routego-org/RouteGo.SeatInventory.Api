using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Api.Common.Behaviors;

/// <summary>
/// Documents the response-metadata headers on every OpenAPI operation so Scalar shows them
/// (references/host-components.md). Registered in the per-Area AddOpenApi loop in Program.cs.
/// Targets Microsoft.OpenApi 2.x (the csproj's [2.12,3.0) range keeps it there): the models live in the
/// Microsoft.OpenApi namespace and OpenApiSchema.Type is the JsonSchemaType enum (a string in 1.6.x).
/// </summary>
public sealed class ResponseHeadersTransformer : IOpenApiOperationTransformer
{
    private sealed record HeaderDoc(string Name, string Description);

    private static readonly HeaderDoc[] _headers = new[]
    {
        new HeaderDoc("X-Trace-Id", "Request trace id (W3C)."),
        new HeaderDoc("Server-Timing", "Server processing time, e.g. app;dur=42.0."),
        new HeaderDoc("X-Server-Time-Epoch", "Server time, Unix epoch seconds."),
    };

    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (operation.Responses is null)
        {
            return Task.CompletedTask;
        }

        foreach (IOpenApiResponse candidate in operation.Responses.Values)
        {
            // 2.x enumerates the read interface; mutate the concrete response.
            if (candidate is not OpenApiResponse response)
            {
                continue;
            }
            response.Headers ??= new Dictionary<string, IOpenApiHeader>();
            foreach (HeaderDoc header in _headers)
            {
                response.Headers.TryAdd(header.Name, new OpenApiHeader
                {
                    Description = header.Description,
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                });
            }
        }
        return Task.CompletedTask;
    }
}
