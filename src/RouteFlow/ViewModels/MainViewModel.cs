using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RouteFlow.Models;
using RouteFlow.Services;

namespace RouteFlow.ViewModels;

public partial class MainViewModel(
    AppPaths paths,
    ConfigurationService configurationService,
    RelayConfigurationService relayConfigurationService,
    SingBoxProcessService processService,
    PrivilegedActionService privilegedActionService) : ViewModelBase
{
    private bool _initialized;
    private bool _isLoading;
    private readonly SemaphoreSlim _autoSaveGate = new(1, 1);
    private CancellationTokenSource? _autoSaveCts;
    private int _clientChangeVersion;
    private int _clientSavedVersion;
    private int _relayChangeVersion;
    private int _relaySavedVersion;

    [ObservableProperty] private string _proxyServer = string.Empty;
    [ObservableProperty] private string _proxyPort = string.Empty;
    [ObservableProperty] private string _proxyUsername = string.Empty;
    [ObservableProperty] private string _proxyPassword = string.Empty;
    [ObservableProperty] private string _dnsServer = string.Empty;
    [ObservableProperty] private string _dnsPort = string.Empty;
    [ObservableProperty] private string _dnsTlsName = string.Empty;
    [ObservableProperty] private string _tunInterface = string.Empty;
    [ObservableProperty] private string _tunAddresses = string.Empty;
    [ObservableProperty] private bool _autoRoute;
    [ObservableProperty] private bool _strictRoute;
    [ObservableProperty] private string _routeAddresses = string.Empty;
    [ObservableProperty] private string _routeDomains = string.Empty;
    [ObservableProperty] private string _relayListenAddress = "0.0.0.0";
    [ObservableProperty] private string _relayListenPort = "7890";
    [ObservableProperty] private string _relayClientAddress = "192.168.63.63";
    [ObservableProperty] private string _relayUsername = string.Empty;
    [ObservableProperty] private string _relayPassword = string.Empty;
    [ObservableProperty] private string _relayTestText = "未测试";
    [ObservableProperty] private RunMode _selectedMode = RunMode.Client;
    [ObservableProperty] private string _processStateText = "正在检测";
    [ObservableProperty] private string _startStopText = "启动";
    [ObservableProperty] private string _statusText = "准备就绪";
    [ObservableProperty] private string _proxyTestText = "未测试";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isBusy;

    public string HomeDirectory => paths.HomeDirectory;
    public string PlatformText => OperatingSystem.IsWindows() ? "Windows" : "Linux";
    public bool IsReady => !IsBusy;
    public bool CanChangeMode => IsReady && !IsRunning;
    public bool IsClientMode => SelectedMode == RunMode.Client;
    public bool IsRelayMode => SelectedMode == RunMode.Relay;
    public string RelayConnectionText => $"HTTP / SOCKS：{RelayClientAddress.Trim()}:{RelayListenPort.Trim()}";

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(CanChangeMode));
    }

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(CanChangeMode));

    partial void OnSelectedModeChanged(RunMode value)
    {
        OnPropertyChanged(nameof(IsClientMode));
        OnPropertyChanged(nameof(IsRelayMode));
    }

    partial void OnProxyServerChanged(string value) => ScheduleClientAutoSave();

    partial void OnProxyPortChanged(string value) => ScheduleClientAutoSave();

    partial void OnProxyUsernameChanged(string value) => ScheduleClientAutoSave();

    partial void OnProxyPasswordChanged(string value) => ScheduleClientAutoSave();

    partial void OnDnsServerChanged(string value) => ScheduleClientAutoSave();

    partial void OnDnsPortChanged(string value) => ScheduleClientAutoSave();

    partial void OnDnsTlsNameChanged(string value) => ScheduleClientAutoSave();

    partial void OnTunInterfaceChanged(string value) => ScheduleClientAutoSave();

    partial void OnTunAddressesChanged(string value) => ScheduleClientAutoSave();

    partial void OnAutoRouteChanged(bool value) => ScheduleClientAutoSave();

    partial void OnStrictRouteChanged(bool value) => ScheduleClientAutoSave();

    partial void OnRouteAddressesChanged(string value) => ScheduleClientAutoSave();

    partial void OnRouteDomainsChanged(string value) => ScheduleClientAutoSave();

    partial void OnRelayListenAddressChanged(string value) => ScheduleRelayAutoSave();

    partial void OnRelayListenPortChanged(string value)
    {
        OnPropertyChanged(nameof(RelayConnectionText));
        ScheduleRelayAutoSave();
    }

    partial void OnRelayClientAddressChanged(string value)
    {
        OnPropertyChanged(nameof(RelayConnectionText));
        ScheduleRelayAutoSave();
    }

    partial void OnRelayUsernameChanged(string value) => ScheduleRelayAutoSave();

    partial void OnRelayPasswordChanged(string value) => ScheduleRelayAutoSave();

    public async Task InitializeAsync()
    {
        if (_initialized)
            return;
        _initialized = true;
        await ReloadAsync();
        await RefreshStatusAsync();
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        await RunBusyAsync(async () =>
        {
            _autoSaveCts?.Cancel();
            await _autoSaveGate.WaitAsync();
            _isLoading = true;
            try
            {
                var configuration = await configurationService.LoadAsync();
                ProxyServer = configuration.ProxyServer;
                ProxyPort = configuration.ProxyPort;
                ProxyUsername = configuration.ProxyUsername;
                ProxyPassword = configuration.ProxyPassword;
                DnsServer = configuration.DnsServer;
                DnsPort = configuration.DnsPort;
                DnsTlsName = configuration.DnsTlsName;
                TunInterface = configuration.TunInterface;
                TunAddresses = configuration.TunAddresses;
                AutoRoute = configuration.AutoRoute;
                StrictRoute = configuration.StrictRoute;
                RouteAddresses = configuration.RouteAddresses;
                RouteDomains = configuration.RouteDomains;
                var relay = await relayConfigurationService.LoadAsync();
                RelayListenAddress = relay.ListenAddress;
                RelayListenPort = relay.ListenPort;
                RelayClientAddress = relay.ClientAddress;
                RelayUsername = relay.Username;
                RelayPassword = relay.Password;
                _clientChangeVersion = _clientSavedVersion = 0;
                _relayChangeVersion = _relaySavedVersion = 0;
                StatusText = "已加载本机分流与内网中转配置";
            }
            finally
            {
                _isLoading = false;
                _autoSaveGate.Release();
            }
        }, "加载失败");
    }

    [RelayCommand]
    private async Task ToggleProcessAsync()
    {
        await RunBusyAsync(async () =>
        {
            var current = await processService.GetStatusAsync();
            if (current.IsRunning)
            {
                await privilegedActionService.ExecuteAsync("stop", SelectedMode);
                StatusText = "sing-box 已停止";
            }
            else
            {
                await FlushPendingChangesAsync(SelectedMode);
                await privilegedActionService.ExecuteAsync("start", SelectedMode);
                StatusText = SelectedMode == RunMode.Relay ? "内网中转已启动" : "本机分流已启动";
            }
            await RefreshStatusAsync();
        }, "操作失败");
    }

    [RelayCommand]
    private async Task RestartAsync()
    {
        await RunBusyAsync(async () =>
        {
            await FlushPendingChangesAsync(SelectedMode);
            await privilegedActionService.ExecuteAsync("restart", SelectedMode);
            StatusText = SelectedMode == RunMode.Relay ? "内网中转已重启" : "本机分流已重启";
            await RefreshStatusAsync();
        }, "重启失败");
    }

    [RelayCommand]
    private async Task TestProxyAsync()
    {
        if (!int.TryParse(ProxyPort, out var port) || port is < 1 or > 65535)
        {
            ProxyTestText = "端口无效";
            return;
        }
        ProxyTestText = "测试中";
        var result = await processService.TestTcpAsync(ProxyServer.Trim(), port);
        ProxyTestText = result.Success ? $"成功，{result.ElapsedMilliseconds} ms" : $"失败：{result.Message}";
    }

    [RelayCommand]
    private async Task TestRelayAsync()
    {
        if (!int.TryParse(RelayListenPort, out var port) || port is < 1 or > 65535)
        {
            RelayTestText = "端口无效";
            return;
        }
        RelayTestText = "测试中";
        var result = await processService.TestTcpAsync(RelayClientAddress.Trim(), port);
        RelayTestText = result.Success ? $"成功，{result.ElapsedMilliseconds} ms" : $"失败：{result.Message}";
    }

    [RelayCommand]
    private void SelectClientMode()
    {
        if (CanChangeMode)
            SelectedMode = RunMode.Client;
    }

    [RelayCommand]
    private void SelectRelayMode()
    {
        if (CanChangeMode)
            SelectedMode = RunMode.Relay;
    }

    public async Task RefreshStatusAsync()
    {
        try
        {
            var status = await processService.GetStatusAsync();
            IsRunning = status.IsRunning;
            if (status.IsRunning && status.Mode is { } runningMode)
                SelectedMode = runningMode;
            var modeName = status.Mode == RunMode.Relay ? "内网中转" : "本机分流";
            ProcessStateText = status.IsRunning ? $"{modeName}运行中 · PID {status.ProcessId}" : "已停止";
            StartStopText = status.IsRunning ? "停止" : "启动";
        }
        catch (Exception exception)
        {
            ProcessStateText = "状态检测失败";
            StatusText = exception.Message;
        }
    }

    private ClientConfigurationInput CreateInput() => new(
        ProxyServer, ProxyPort, ProxyUsername, ProxyPassword,
        DnsServer, DnsPort, DnsTlsName,
        TunInterface, TunAddresses, AutoRoute, StrictRoute,
        RouteAddresses, RouteDomains);

    public Task FlushPendingChangesAsync() => FlushPendingChangesAsync(null);

    private async Task FlushPendingChangesAsync(RunMode? mode)
    {
        if (mode is null)
            _autoSaveCts?.Cancel();
        await _autoSaveGate.WaitAsync();
        try
        {
            var saved = false;
            if (mode is null or RunMode.Client && _clientSavedVersion != _clientChangeVersion)
            {
                var version = _clientChangeVersion;
                var input = CreateInput();
                await configurationService.SaveAsync(input);
                _clientSavedVersion = version;
                saved = true;
            }

            if (mode is null or RunMode.Relay && _relaySavedVersion != _relayChangeVersion)
            {
                var version = _relayChangeVersion;
                var input = new RelayConfigurationInput(
                    RelayListenAddress,
                    RelayListenPort,
                    RelayClientAddress,
                    RelayUsername,
                    RelayPassword);
                await relayConfigurationService.SaveAsync(input);
                _relaySavedVersion = version;
                saved = true;
            }

            if (saved)
                StatusText = IsRunning ? "配置已自动保存，重启后生效" : "配置已自动保存";
        }
        finally
        {
            _autoSaveGate.Release();
        }
    }

    private void ScheduleClientAutoSave()
    {
        if (_isLoading)
            return;
        _clientChangeVersion++;
        ScheduleAutoSave();
    }

    private void ScheduleRelayAutoSave()
    {
        if (_isLoading)
            return;
        _relayChangeVersion++;
        ScheduleAutoSave();
    }

    private void ScheduleAutoSave()
    {
        _autoSaveCts?.Cancel();
        _autoSaveCts = new CancellationTokenSource();
        _ = AutoSaveAfterDelayAsync(_autoSaveCts.Token);
    }

    private async Task AutoSaveAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(650), cancellationToken);
            await FlushPendingChangesAsync();
        }
        catch (OperationCanceledException)
        {
            // A newer edit restarted the debounce interval.
        }
        catch (Exception exception)
        {
            StatusText = $"自动保存失败：{exception.Message}";
        }
    }

    private async Task RunBusyAsync(Func<Task> action, string failurePrefix)
    {
        if (IsBusy)
            return;
        IsBusy = true;
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            StatusText = $"{failurePrefix}：{exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
