using System.Drawing;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using NetTrace.Core.Monitoring;
using NetTrace.Core.Throughput;
using Forms = System.Windows.Forms;

namespace NetTrace.App.Services;

/// <summary>
/// System tray icon. When live throughput is enabled the icon shows the current download rate
/// (plan.md section 4.11) and the tooltip shows both rates, the tracked connection and the
/// monitoring state. Updates happen on the UI thread at most once per throughput sample.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const int MaxTooltipLength = 127;

    private readonly IThroughputSource _throughput;
    private readonly IMonitoringStatusSource _status;
    private readonly IApplicationShell _shell;
    private SynchronizationContext? _uiContext;
    private Forms.NotifyIcon? _notifyIcon;
    private Icon? _appIcon;
    private Icon? _rateIcon;
    private Forms.ToolStripMenuItem? _statusItem;
    private Forms.ToolStripMenuItem? _rateItem;
    private string? _renderedText;
    private bool _hideNoticeShown;

    public TrayIconService(IThroughputSource throughput, IMonitoringStatusSource status, IApplicationShell shell)
    {
        _throughput = throughput;
        _status = status;
        _shell = shell;
    }

    /// <summary>Creates the tray icon. Must be called on the UI thread.</summary>
    public void Initialize(Action openWindow)
    {
        ArgumentNullException.ThrowIfNull(openWindow);
        _uiContext = SynchronizationContext.Current;
        _appIcon = LoadAppIcon();

        _statusItem = new Forms.ToolStripMenuItem { Enabled = false };
        _rateItem = new Forms.ToolStripMenuItem { Enabled = false };
        var open = new Forms.ToolStripMenuItem("Open NetTrace", null, (_, _) => openWindow())
        {
            Font = new Font(Forms.Control.DefaultFont, System.Drawing.FontStyle.Bold),
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.AddRange(
        [
            open,
            new Forms.ToolStripSeparator(),
            _statusItem,
            _rateItem,
            new Forms.ToolStripSeparator(),
            new Forms.ToolStripMenuItem("Exit", null, (_, _) => _shell.RequestExit()),
        ]);

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _appIcon,
            Text = "NetTrace",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                openWindow();
            }
        };

        _throughput.SampleAvailable += OnSample;
        _status.StatusChanged += OnStatus;
        Refresh();
    }

    /// <summary>Tells the user once per session that closing the window keeps NetTrace running.</summary>
    public void NotifyHiddenToTray()
    {
        if (_hideNoticeShown || _notifyIcon is null)
        {
            return;
        }

        _hideNoticeShown = true;
        _notifyIcon.ShowBalloonTip(
            4000,
            "NetTrace is still running",
            "Monitoring continues in the background. Use the tray icon to open NetTrace or exit.",
            Forms.ToolTipIcon.None);
    }

    public void Dispose()
    {
        _throughput.SampleAvailable -= OnSample;
        _status.StatusChanged -= OnStatus;
        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.ContextMenuStrip?.Dispose();
            _notifyIcon.Dispose();
        }

        ReleaseRateIcon();
        _appIcon?.Dispose();
    }

    private void OnSample(object? sender, ThroughputSample sample) => PostRefresh();

    private void OnStatus(object? sender, MonitoringStatus status) => PostRefresh();

    private void PostRefresh() => _uiContext?.Post(_ => Refresh(), null);

    private void Refresh()
    {
        if (_notifyIcon is null || _statusItem is null || _rateItem is null)
        {
            return;
        }

        var configuration = _throughput.Configuration;
        var sample = _throughput.Latest;
        var state = DescribeState(_status.Current.State);

        string rateLine;
        string? iconText = null;
        if (!configuration.Enabled)
        {
            rateLine = "Live usage display is off";
        }
        else if (sample is { IsAvailable: true })
        {
            var down = RateFormatter.Format(sample.DownloadBytesPerSecond, configuration.Unit);
            var up = RateFormatter.Format(sample.UploadBytesPerSecond, configuration.Unit);
            rateLine = $"↓ {down}  ↑ {up}";
            if (configuration.ShowConnectionLabel)
            {
                rateLine += " - " + RateFormatter.DescribeKind(sample.InterfaceKind);
            }

            iconText = configuration.ShowRateInTrayIcon ? down : null;
        }
        else
        {
            rateLine = "Usage unavailable" + (sample?.UnavailableReason is { } reason ? $" ({reason})" : string.Empty);
        }

        _statusItem.Text = "Monitoring: " + state;
        _rateItem.Text = "Current usage: " + rateLine;

        var tooltip = $"NetTrace - {state}\n{rateLine}";
        _notifyIcon.Text = tooltip.Length > MaxTooltipLength ? tooltip[..MaxTooltipLength] : tooltip;

        if (iconText != _renderedText)
        {
            _renderedText = iconText;
            var previous = _rateIcon;
            _rateIcon = iconText is null ? null : RenderTextIcon(iconText);
            _notifyIcon.Icon = _rateIcon ?? _appIcon;
            ReleaseIcon(previous);
        }
    }

    private static string DescribeState(MonitoringState state) => state switch
    {
        MonitoringState.Running => "On",
        MonitoringState.Disabled => "Paused",
        MonitoringState.Faulted => "Stopped (error)",
        MonitoringState.Stopped => "Stopped",
        _ => "Starting",
    };

    private static Icon LoadAppIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/NetTrace.ico"))
            ?? throw new InvalidOperationException("The application icon resource is missing.");
        using var stream = resource.Stream;
        return new Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    /// <summary>Draws the rate as text sized to the tray's small-icon size, contrasting with the taskbar theme.</summary>
    private static Icon RenderTextIcon(string text)
    {
        var size = Forms.SystemInformation.SmallIconSize.Width;
        using var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            // ClearType cannot blend onto a transparent background; grayscale antialiasing can.
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var brush = new SolidBrush(IsTaskbarLight() ? Color.Black : Color.White);
            using var format = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap,
            };

            // Largest bold font whose text fits the icon width.
            var emSize = size * 0.75f;
            Font font;
            while (true)
            {
                font = new Font("Segoe UI", emSize, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
                if (graphics.MeasureString(text, font, int.MaxValue, format).Width <= size || emSize <= 6)
                {
                    break;
                }

                font.Dispose();
                emSize -= 0.5f;
            }

            using (font)
            {
                graphics.DrawString(text, font, brush, new RectangleF(0, 0, size, size), format);
            }
        }

        return Icon.FromHandle(bitmap.GetHicon());
    }

    private static bool IsTaskbarLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int value && value == 1;
    }

    private void ReleaseRateIcon()
    {
        ReleaseIcon(_rateIcon);
        _rateIcon = null;
    }

    private void ReleaseIcon(Icon? icon)
    {
        if (icon is null || ReferenceEquals(icon, _appIcon))
        {
            return;
        }

        // Icon.FromHandle does not own the HICON created by GetHicon; free it explicitly.
        var handle = icon.Handle;
        icon.Dispose();
        NativeMethods.DestroyIcon(handle);
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyIcon(IntPtr handle);
    }
}
