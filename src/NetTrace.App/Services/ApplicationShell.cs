using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;

namespace NetTrace.App.Services;

/// <summary>Application-level actions view models can request without referencing WPF types.</summary>
public interface IApplicationShell
{
    /// <summary>True once an explicit exit was requested; window close then exits instead of hiding to the tray.</summary>
    bool IsExiting { get; }

    void RequestExit();

    void OpenFolder(string path);

    /// <summary>Asks a yes/no question on the UI thread. Defaults to No.</summary>
    bool Confirm(string title, string message);
}

internal sealed class WpfApplicationShell : IApplicationShell
{
    public bool IsExiting { get; private set; }

    public void RequestExit()
    {
        IsExiting = true;
        Application.Current.Shutdown(0);
    }

    public bool Confirm(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    public void OpenFolder(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path))
        {
            return;
        }

        using var _ = Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            ArgumentList = { path },
            UseShellExecute = false,
        });
    }
}

internal static class ApplicationInfo
{
    /// <summary>Product version without source-control build metadata (for example "0.1.0").</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var informational = typeof(ApplicationInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(informational))
        {
            return typeof(ApplicationInfo).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        }

        var metadataStart = informational.IndexOf('+', StringComparison.Ordinal);
        return metadataStart >= 0 ? informational[..metadataStart] : informational;
    }
}
