namespace NetTrace.Infrastructure.Storage;

/// <summary>
/// Locations of mutable per-user application data. Defaults to %LOCALAPPDATA%\NetTrace.
/// Nothing is ever written beside the executable.
/// </summary>
public sealed class AppDataPaths
{
    public AppDataPaths(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        if (!Path.IsPathFullyQualified(rootDirectory))
        {
            throw new ArgumentException("The application data root must be an absolute path.", nameof(rootDirectory));
        }

        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    public string RootDirectory { get; }

    public string DatabasePath => Path.Combine(RootDirectory, "nettrace.db");

    public string LogsDirectory => Path.Combine(RootDirectory, "Logs");

    public string ExportsDirectory => Path.Combine(RootDirectory, "Exports");

    public static AppDataPaths CreateDefault()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);
        return new AppDataPaths(Path.Combine(localAppData, "NetTrace"));
    }

    /// <summary>Creates the data directories if they are missing. Existing data is never touched.</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(ExportsDirectory);
    }
}
