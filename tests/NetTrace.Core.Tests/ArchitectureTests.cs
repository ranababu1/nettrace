using NetTrace.Core.Time;

namespace NetTrace.Core.Tests;

public class ArchitectureTests
{
    private static readonly string[] ForbiddenReferencePrefixes =
    [
        "PresentationFramework",
        "PresentationCore",
        "WindowsBase",
        "System.Windows",
        "Microsoft.Data.Sqlite",
        "Microsoft.EntityFrameworkCore",
        "System.Net.Http",
        "System.Net.NetworkInformation",
        "System.Net.Ping",
        "NetTrace.Infrastructure",
        "NetTrace.Reporting",
        "NetTrace.App",
    ];

    [Fact]
    public void Core_DoesNotReferenceUiPersistenceOrNetworkImplementations()
    {
        var references = typeof(IClock).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty);

        var violations = references
            .Where(name => ForbiddenReferencePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToList();

        Assert.Empty(violations);
    }
}
