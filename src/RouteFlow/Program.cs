using Avalonia;
using System;
using RouteFlow.Models;
using RouteFlow.Services;

namespace RouteFlow;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && string.Equals(args[0], "--self-test", StringComparison.OrdinalIgnoreCase))
            return await RunSelfTestAsync();
        if (args.Length == 1 && TryParseProcessAction(args[0], out var action, out var mode))
            return await RunProcessActionAsync(action, mode);

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static async Task<int> RunSelfTestAsync()
    {
        try
        {
            var paths = AppPaths.Resolve();
            var processService = new SingBoxProcessService(paths);
            var configurationService = new ConfigurationService(paths, processService);
            var relayConfigurationService = new RelayConfigurationService(paths, processService);
            await configurationService.LoadAsync();
            await relayConfigurationService.ValidateDefaultsAsync();
            var check = await processService.CheckConfigurationAsync(paths.ConfigurationPath);
            if (!check.Success)
                throw new InvalidDataException(check.Message);
            return 0;
        }
        catch (Exception exception)
        {
            await File.WriteAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, ".self-test-error.log"),
                exception.ToString());
            return 1;
        }
    }

    private static async Task<int> RunProcessActionAsync(string action, RunMode mode)
    {
        try
        {
            var paths = AppPaths.Resolve();
            var processService = new SingBoxProcessService(paths);
            var privilegedActionService = new PrivilegedActionService(paths, processService);
            await privilegedActionService.ExecuteDirectAsync(action, mode);
            return 0;
        }
        catch (Exception exception)
        {
            var paths = AppPaths.Resolve();
            Directory.CreateDirectory(paths.RuntimeDirectory);
            await File.WriteAllTextAsync(paths.StandardErrorLogPath, exception.ToString());
            return 1;
        }
    }

    private static bool TryParseProcessAction(string argument, out string action, out RunMode mode)
    {
        action = string.Empty;
        mode = argument.EndsWith("-relay", StringComparison.OrdinalIgnoreCase) ? RunMode.Relay : RunMode.Client;
        if (string.Equals(argument, "--stop", StringComparison.OrdinalIgnoreCase))
        {
            action = "stop";
            return true;
        }
        if (argument.StartsWith("--start", StringComparison.OrdinalIgnoreCase))
            action = "start";
        else if (argument.StartsWith("--restart", StringComparison.OrdinalIgnoreCase))
            action = "restart";
        return action.Length > 0;
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
