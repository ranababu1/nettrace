using NetTrace.Infrastructure.Storage;

namespace NetTrace.Infrastructure.Tests;

public class AppDataPathsTests
{
    [Fact]
    public void Default_IsUnderLocalApplicationData()
    {
        var paths = AppDataPaths.CreateDefault();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.Equal(Path.Combine(localAppData, "NetTrace"), paths.RootDirectory);
        Assert.DoesNotContain(AppContext.BaseDirectory.TrimEnd('\\'), paths.RootDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Layout_PlacesDatabaseLogsAndExportsUnderRoot()
    {
        var paths = new AppDataPaths(@"C:\Data\NetTrace");

        Assert.Equal(@"C:\Data\NetTrace\nettrace.db", paths.DatabasePath);
        Assert.Equal(@"C:\Data\NetTrace\Logs", paths.LogsDirectory);
        Assert.Equal(@"C:\Data\NetTrace\Exports", paths.ExportsDirectory);
    }

    [Theory]
    [InlineData("relative\\path")]
    [InlineData("C:relative")]
    [InlineData(" ")]
    public void Constructor_RejectsNonAbsoluteRoots(string root)
    {
        Assert.ThrowsAny<ArgumentException>(() => new AppDataPaths(root));
    }

    [Fact]
    public void EnsureCreated_CreatesDirectoriesAndPreservesExistingFiles()
    {
        using var temp = new TempDirectory();
        var paths = new AppDataPaths(Path.Combine(temp.Path, "NetTrace"));
        paths.EnsureCreated();
        var existingFile = Path.Combine(paths.ExportsDirectory, "report.json");
        File.WriteAllText(existingFile, "keep");

        paths.EnsureCreated();

        Assert.True(Directory.Exists(paths.LogsDirectory));
        Assert.True(Directory.Exists(paths.ExportsDirectory));
        Assert.Equal("keep", File.ReadAllText(existingFile));
    }
}
