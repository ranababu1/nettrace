using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace NetTrace.Infrastructure.Startup;

/// <summary>Per-user launch-at-login registration. No elevation, no scheduled task, no service.</summary>
public interface IStartupRegistration
{
    bool IsEnabled { get; }

    /// <summary>Registers (or re-registers, never duplicating) the application to start at login.</summary>
    void Enable(string executablePath, bool startMinimized);

    void Disable();
}

/// <summary>
/// Uses the current user's <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> key with a
/// single named value. Command-line arguments are limited to the fixed <c>--minimized</c> flag.
/// </summary>
public sealed class RegistryStartupRegistration(ILogger<RegistryStartupRegistration> logger, string valueName = "NetTrace")
    : IStartupRegistration
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string MinimizedArgument = "--minimized";

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(valueName) is string;
        }
    }

    public void Enable(string executablePath, bool startMinimized)
    {
        if (!Path.IsPathFullyQualified(executablePath))
        {
            throw new ArgumentException("The executable path must be absolute.", nameof(executablePath));
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        key.SetValue(valueName, BuildCommand(executablePath, startMinimized), RegistryValueKind.String);
        logger.LogInformation("Launch at login enabled (start minimized: {StartMinimized}).", startMinimized);
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key?.GetValue(valueName) is not null)
        {
            key.DeleteValue(valueName, throwOnMissingValue: false);
            logger.LogInformation("Launch at login disabled.");
        }
    }

    public static string BuildCommand(string executablePath, bool startMinimized) =>
        "\"" + executablePath + "\"" + (startMinimized ? " " + MinimizedArgument : string.Empty);
}
