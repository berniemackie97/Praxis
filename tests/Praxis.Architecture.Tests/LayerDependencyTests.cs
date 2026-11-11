using System.Reflection;
using Xunit;

namespace Praxis.Architecture.Tests;

/// <summary>
/// Project references follow the layering: Domain depends on nothing, Application only on
/// Domain, Infrastructure never on a host, and the portable notes library stands alone.
/// </summary>
public sealed class LayerDependencyTests
{
    [Fact]
    public void DomainReferencesOnlyTheBaseClassLibrary() =>
        Assert.DoesNotContain(References("Praxis.Domain"), name => !IsBaseClassLibrary(name));

    [Fact]
    public void ApplicationReferencesOnlyDomainAndTheBaseClassLibrary() =>
        Assert.DoesNotContain(References("Praxis.Application"), name => name != "Praxis.Domain" && !IsBaseClassLibrary(name));

    [Fact]
    public void InfrastructureDoesNotReferenceHosts() =>
        Assert.DoesNotContain(References("Praxis.Infrastructure"), name => name is "Praxis.Worker" or "Praxis.Api");

    [Fact]
    public void NotesLibraryIsIndependentOfTheCatalogLayers() =>
        Assert.DoesNotContain(References("Praxis.Notes"), name => name.StartsWith("Praxis.", StringComparison.Ordinal));

    private static IEnumerable<string> References(string assembly) =>
        Assembly.Load(assembly).GetReferencedAssemblies().Select(reference => reference.Name!);

    private static bool IsBaseClassLibrary(string name) =>
        name is "netstandard" or "mscorlib" || name.StartsWith("System", StringComparison.Ordinal);
}
