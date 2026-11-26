using System.Reflection;
using Zirrus.Application.Catalogs;
using Zirrus.Domain.Catalogs;

namespace Zirrus.Domain.Tests;

/// <summary>
/// Layer rules from system-architecture.md: the domain depends on nothing but the BCL, and the
/// application layer only on the domain. Project references enforce this too; these tests make a
/// violation explicit if someone adds a package reference.
/// </summary>
public sealed class LayerDependencyTests
{
    [Fact]
    public void Domain_references_only_the_base_class_library() =>
        Assert.Empty(NonSystemReferences(typeof(Catalog).Assembly));

    [Fact]
    public void Application_references_only_the_domain()
    {
        // The compiler drops references whose types are unused, so check "nothing but the domain".
        var references = NonSystemReferences(typeof(ListCatalogs).Assembly);
        Assert.All(references, name => Assert.Equal("Zirrus.Domain", name));
    }

    private static string[] NonSystemReferences(Assembly assembly) =>
        assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n != "netstandard" && !n.StartsWith("System", StringComparison.Ordinal)
                        && !n.StartsWith("Microsoft.Win32", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
}
