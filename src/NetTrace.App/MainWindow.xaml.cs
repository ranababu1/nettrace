using System.Windows;
using NetTrace.App.ViewModels;

namespace NetTrace.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
