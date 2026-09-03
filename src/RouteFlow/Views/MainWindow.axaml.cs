using Avalonia.Controls;
using Avalonia.Threading;
using RouteFlow.ViewModels;

namespace RouteFlow.Views;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        Closing += OnClosing;
        Closed += (_, _) => _statusTimer.Stop();
        _statusTimer.Tick += async (_, _) =>
        {
            if (DataContext is MainViewModel viewModel)
                await viewModel.RefreshStatusAsync();
        };
    }

    private async void OnOpened(object? sender, EventArgs eventArgs)
    {
        if (DataContext is not MainViewModel viewModel)
            return;
        await viewModel.InitializeAsync();
        _statusTimer.Start();
    }

    public void AllowClose() => _allowClose = true;

    private void OnClosing(object? sender, WindowClosingEventArgs eventArgs)
    {
        if (_allowClose || eventArgs.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)
            return;
        eventArgs.Cancel = true;
        Hide();
    }
}
