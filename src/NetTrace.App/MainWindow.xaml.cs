using System.ComponentModel;
using System.Windows;
using NetTrace.App.Services;
using NetTrace.App.ViewModels;

namespace NetTrace.App;

public partial class MainWindow : Window
{
    private readonly IApplicationShell _shell;

    public MainWindow(MainViewModel viewModel, IApplicationShell shell)
    {
        InitializeComponent();
        DataContext = viewModel;
        _shell = shell;
    }

    /// <summary>Raised when closing the window hid it to the tray instead of exiting.</summary>
    public event EventHandler? HiddenToTray;

    protected override void OnClosing(CancelEventArgs e)
    {
        // Monitoring is independent of the window: closing hides to the tray unless Exit was chosen.
        if (!_shell.IsExiting)
        {
            e.Cancel = true;
            Hide();
            HiddenToTray?.Invoke(this, EventArgs.Empty);
        }

        base.OnClosing(e);
    }
}
