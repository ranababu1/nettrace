using System.Globalization;
using System.Windows.Input;
using NetTrace.App.Services;
using NetTrace.Core.Time;
using NetTrace.Infrastructure.Storage;

namespace NetTrace.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    public MainViewModel(AppDataPaths paths, IClock clock, IApplicationShell shell)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(shell);

        DataDirectory = paths.RootDirectory;
        var startedLocal = TimeZoneInfo.ConvertTime(clock.UtcNow, clock.LocalTimeZone);
        StartedAtText = startedLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)
            + " (" + clock.LocalTimeZone.DisplayName + ")";

        ExitCommand = new RelayCommand(shell.RequestExit);
        OpenDataFolderCommand = new RelayCommand(() => shell.OpenFolder(DataDirectory));
    }

    public string Title => "NetTrace";

    public string ApplicationVersion => ApplicationInfo.Version;

    public string DataDirectory { get; }

    public string StartedAtText { get; }

    // Monitoring is not implemented in this build, so the honest state is Unmonitored.
    public string MonitoringState => "Unmonitored";

    public string MonitoringStateDescription =>
        "No network measurements are being recorded. The monitoring engine is not available in this build yet.";

    public ICommand ExitCommand { get; }

    public ICommand OpenDataFolderCommand { get; }
}
