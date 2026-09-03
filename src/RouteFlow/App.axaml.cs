using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using RouteFlow.Models;
using RouteFlow.Services;
using RouteFlow.ViewModels;
using RouteFlow.Views;

namespace RouteFlow;

public partial class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private MainWindow? _mainWindow;
    private MainViewModel? _viewModel;
    private TrayIcon? _trayIcon;
    private NativeMenuItem? _toggleItem;
    private NativeMenuItem? _restartItem;
    private NativeMenuItem? _clientModeItem;
    private NativeMenuItem? _relayModeItem;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var paths = AppPaths.Resolve();
            var processService = new SingBoxProcessService(paths);
            var configurationService = new ConfigurationService(paths, processService);
            var relayConfigurationService = new RelayConfigurationService(paths, processService);
            var privilegedActionService = new PrivilegedActionService(paths, processService);
            _viewModel = new MainViewModel(
                paths,
                configurationService,
                relayConfigurationService,
                processService,
                privilegedActionService);
            if (Environment.GetCommandLineArgs()
                .Any(argument => string.Equals(argument, "--relay", StringComparison.OrdinalIgnoreCase)))
                _viewModel.SelectedMode = RunMode.Relay;
            _mainWindow = new MainWindow
            {
                DataContext = _viewModel,
            };
            desktop.MainWindow = _mainWindow;
            CreateTrayIcon();
            _viewModel.PropertyChanged += (_, _) => UpdateTrayState();
            desktop.Exit += (_, _) => DisposeTrayIcon();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void CreateTrayIcon()
    {
        var showItem = new NativeMenuItem("显示主界面");
        showItem.Click += (_, _) => ShowMainWindow();

        _toggleItem = new NativeMenuItem("启动本机分流");
        _toggleItem.Click += async (_, _) =>
        {
            if (_viewModel?.ToggleProcessCommand.CanExecute(null) == true)
                await _viewModel.ToggleProcessCommand.ExecuteAsync(null);
            UpdateTrayState();
        };

        _restartItem = new NativeMenuItem("重启");
        _restartItem.Click += async (_, _) =>
        {
            if (_viewModel?.RestartCommand.CanExecute(null) == true)
                await _viewModel.RestartCommand.ExecuteAsync(null);
            UpdateTrayState();
        };

        _clientModeItem = new NativeMenuItem("本机分流") { ToggleType = NativeMenuItemToggleType.Radio };
        _clientModeItem.Click += (_, _) => SelectMode(RunMode.Client);
        _relayModeItem = new NativeMenuItem("内网中转") { ToggleType = NativeMenuItemToggleType.Radio };
        _relayModeItem.Click += (_, _) => SelectMode(RunMode.Relay);

        var exitItem = new NativeMenuItem("退出托盘程序");
        exitItem.Click += async (_, _) =>
        {
            try
            {
                if (_viewModel is not null)
                    await _viewModel.FlushPendingChangesAsync();
                ExitApplication();
            }
            catch (Exception exception)
            {
                if (_viewModel is not null)
                    _viewModel.StatusText = $"自动保存失败：{exception.Message}";
                ShowMainWindow();
            }
        };

        var menu = new NativeMenu();
        menu.Add(showItem);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(_toggleItem);
        menu.Add(_restartItem);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(_clientModeItem);
        menu.Add(_relayModeItem);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(exitItem);

        using var iconStream = AssetLoader.Open(new Uri("avares://RouteFlow/Assets/routeflow.ico"));
        _trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(iconStream),
            Menu = menu,
            ToolTipText = "RouteFlow",
            IsVisible = true,
        };
        _trayIcon.Clicked += (_, _) => ShowMainWindow();
        TrayIcon.SetIcons(this, new TrayIcons { _trayIcon });
        UpdateTrayState();
    }

    private void SelectMode(RunMode mode)
    {
        if (_viewModel?.CanChangeMode == true)
            _viewModel.SelectedMode = mode;
        UpdateTrayState();
    }

    private void UpdateTrayState()
    {
        if (_viewModel is null || _trayIcon is null || _toggleItem is null ||
            _restartItem is null || _clientModeItem is null || _relayModeItem is null)
            return;

        var modeName = _viewModel.SelectedMode == RunMode.Relay ? "内网中转" : "本机分流";
        _trayIcon.ToolTipText = $"RouteFlow - {_viewModel.ProcessStateText}";
        _toggleItem.Header = _viewModel.IsRunning ? $"停止{modeName}" : $"启动{modeName}";
        _toggleItem.IsEnabled = _viewModel.IsReady;
        _restartItem.IsEnabled = _viewModel.IsReady && _viewModel.IsRunning;
        _clientModeItem.IsChecked = _viewModel.SelectedMode == RunMode.Client;
        _relayModeItem.IsChecked = _viewModel.SelectedMode == RunMode.Relay;
        _clientModeItem.IsEnabled = _viewModel.CanChangeMode;
        _relayModeItem.IsEnabled = _viewModel.CanChangeMode;
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null)
            return;
        if (!_mainWindow.IsVisible)
            _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private void ExitApplication()
    {
        _mainWindow?.AllowClose();
        DisposeTrayIcon();
        _desktop?.Shutdown();
    }

    private void DisposeTrayIcon()
    {
        if (_trayIcon is null)
            return;
        _trayIcon.IsVisible = false;
        _trayIcon.Dispose();
        _trayIcon = null;
        TrayIcon.SetIcons(this, new TrayIcons());
    }
}
