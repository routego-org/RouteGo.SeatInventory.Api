
namespace Api.Common;

/// <summary>
/// The ONE wire-format contract for JSON this app reads or writes outside ASP.NET Core's own
/// response pipeline: HttpClient calls (System.Net.Http.Json overloads take it), event-payload
/// deserialization in a Lambda worker, any hand-rolled JsonSerializer use. camelCase property
/// names, camelCase string enums, case-insensitive reads — exactly the names the authored
/// API/event contracts declare. ASP.NET Core's own response serialization gets the same enum
/// policy via AddJsonOptions in Program.cs, so authored contract, responses, and reads all
/// agree by construction.
/// Never build per-call options (a new JsonSerializerOptions per call discards the serializer's
/// internal cache) and never use [JsonPropertyName]/[JsonStringEnumMemberName] overrides —
/// contract names map 1:1 by policy, not by attribute.
/// </summary>
public static class WireJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
