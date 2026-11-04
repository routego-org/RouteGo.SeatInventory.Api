using System.Reflection;

namespace Api.Tests;

// VSA's substitute for compile fences (SKILL.md). Reflection is sanctioned HERE only — it lives
// in the test assembly and never ships. See skills-aspnetcore-project-setup/references/architecture-test.md.
// Test names follow the standard's T1 (Method_Scenario_ExpectedResult); the test project's
// .editorconfig relaxes the naming rule accordingly.
public sealed class ArchitectureTests
{
    private static readonly Assembly _app = typeof(Program).Assembly;

    // Every distinct slice namespace, e.g. "Api.Features.Orders.PlaceOrder".
    public static IEnumerable<object[]> FeatureNamespaces()
    {
        var slices = new HashSet<string>(); // var: type apparent from the right (F14)
        foreach (Type type in _app.GetTypes())
        {
            string? ns = type.Namespace;
            if (ns is null)
            {
                continue;
            }
            string[] parts = ns.Split('.');
            int features = Array.IndexOf(parts, "Features");
            if (features < 0 || parts.Length < features + 3)
            {
                continue; // not {Root}.Features.{Area}.{Action}
            }
            slices.Add(string.Join('.', parts.Take(features + 3)));
        }
        if (slices.Count == 0)
        {
            return new[] { new object[] { "__none__" } }; // fresh scaffold: keep the suite green
        }
        return slices.Select(ns => new object[] { ns });
    }

    [Theory, MemberData(nameof(FeatureNamespaces))]
    public void Slices_do_not_reference_other_slices(string sliceNs)
    {
        if (sliceNs == "__none__")
        {
            return; // no slices yet
        }
        string[] others = _app.GetTypes()
            .Where(t => t.Namespace?.Contains(".Features.") == true &&
                        !t.Namespace!.StartsWith(sliceNs))
            .Select(t => t.Namespace!).Distinct().ToArray();

        NetArchTest.Rules.TestResult result = NetArchTest.Rules.Types.InAssembly(_app)
            .That().ResideInNamespaceStartingWith(sliceNs)
            .ShouldNot().HaveDependencyOnAny(others)
            .GetResult();
        Assert.True(result.IsSuccessful,
            $"{sliceNs} leaks into: {string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>())}");
    }

    [Fact]
    public void Stateless_no_in_process_cache()
    {
        NetArchTest.Rules.TestResult result = NetArchTest.Rules.Types.InAssembly(_app)
            .ShouldNot().HaveDependencyOn("Microsoft.Extensions.Caching.Memory")
            .GetResult();
        Assert.True(result.IsSuccessful,
            $"in-process cache isn't shared across execution environments — use IDistributedCache: {string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>())}");
    }

    [Fact]
    public void Domain_depends_on_nothing()
    {
        // swap "Npgsql"/"Dapper" for the project's actual driver packages (Postgres skill)
        NetArchTest.Rules.TestResult result = NetArchTest.Rules.Types.InAssembly(_app)
            .That().ResideInNamespaceStartingWith("Api.Domain")
            .ShouldNot().HaveDependencyOnAny(
                "Api.Features", "Api.Common", "Api.Infrastructure", "Npgsql", "Dapper")
            .GetResult();
        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>()));
    }

    // The seat store is DynamoDB, not Postgres: slice handlers query it directly (as a Postgres
    // slice writes its SQL inline), so the boundary that matters is that Domain stays pure.
    [Fact]
    public void Domain_does_not_touch_the_seat_store()
    {
        NetArchTest.Rules.TestResult result = NetArchTest.Rules.Types.InAssembly(_app)
            .That().ResideInNamespaceStartingWith("Api.Domain")
            .ShouldNot().HaveDependencyOn("Amazon.DynamoDBv2")
            .GetResult();
        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>()));
    }

    [Fact]
    public void Only_infrastructure_touches_the_db_driver()
    {
        NetArchTest.Rules.TestResult result = NetArchTest.Rules.Types.InAssembly(_app)
            .That().DoNotResideInNamespaceStartingWith("Api.Infrastructure")
            .ShouldNot().HaveDependencyOn("Npgsql")
            .GetResult();
        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>()));
    }
}
