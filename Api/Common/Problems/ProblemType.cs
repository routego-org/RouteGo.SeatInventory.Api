namespace Api.Common.Problems;

/// <summary>
/// One cataloged problem type: the stable machine-readable code clients switch on and the
/// type URI that identifies it in RFC 7807 responses.
/// </summary>
public sealed record ProblemType(string Code, string Type);
