using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using RouteFlow.Models;

namespace RouteFlow.Services;

public sealed class PrivilegedActionService(AppPaths paths, SingBoxProcessService processService)
{
    public async Task ExecuteAsync(string action, RunMode mode, CancellationToken cancellationToken = default)
    {
        if (IsElevated())
        {
            await ExecuteDirectAsync(action, mode, cancellationToken);
            return;
        }

        using var helper = StartElevatedHelper(action, mode);
        await helper.WaitForExitAsync(cancellationToken);
        if (helper.ExitCode != 0)
            throw new InvalidOperationException($"特权操作失败，退出码 {helper.ExitCode}。");
    }

    public async Task ExecuteDirectAsync(string action, RunMode mode, CancellationToken cancellationToken = default)
    {
        try
        {
            switch (action.ToLowerInvariant())
            {
                case "start":
                    await processService.StartAsync(ConfigurationPath(mode), mode, cancellationToken);
                    break;
                case "stop":
                    await processService.StopAsync(cancellationToken);
                    break;
                case "restart":
                    await processService.StopAsync(cancellationToken);
                    await processService.StartAsync(ConfigurationPath(mode), mode, cancellationToken);
                    break;
                case "repair-permissions":
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(action), action, "不支持的操作。");
            }
        }
        finally
        {
            RestoreRuntimeOwnership();
        }
    }

    public Task RepairRuntimePermissionsAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync("repair-permissions", RunMode.Client, cancellationToken);

    private Process StartElevatedHelper(string action, RunMode mode)
    {
        var (executable, leadingArguments) = GetCurrentCommand();
        ProcessStartInfo startInfo;
        if (OperatingSystem.IsWindows())
        {
            startInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = paths.HomeDirectory,
                UseShellExecute = true,
                Verb = "runas",
            };
        }
        else if (OperatingSystem.IsLinux())
        {
            startInfo = new ProcessStartInfo
            {
                FileName = "pkexec",
                WorkingDirectory = paths.HomeDirectory,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("env");
            startInfo.ArgumentList.Add($"SING_BOX_HOME={paths.HomeDirectory}");
            startInfo.ArgumentList.Add($"SING_BOX_PATH={paths.SingBoxPath}");
            startInfo.ArgumentList.Add(executable);
        }
        else
        {
            throw new PlatformNotSupportedException("仅支持 Windows 和 Linux。");
        }

        foreach (var argument in leadingArguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.ArgumentList.Add(action == "stop" ? "--stop" : $"--{action}-{ModeValue(mode)}");

        try
        {
            return Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动特权助手。");
        }
        catch (Win32Exception exception) when (OperatingSystem.IsWindows() && exception.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("已取消管理员授权。", exception);
        }
    }

    private static (string Executable, IReadOnlyList<string> LeadingArguments) GetCurrentCommand()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定当前程序路径。");
        var assemblyPath = Assembly.GetEntryAssembly()?.Location;
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(assemblyPath))
            return (executable, new[] { assemblyPath });
        return (executable, Array.Empty<string>());
    }

    private static bool IsElevated()
    {
        if (OperatingSystem.IsWindows())
            return IsUserAnAdmin();
        if (OperatingSystem.IsLinux())
            return GetEffectiveUserId() == 0;
        return false;
    }

    private string ConfigurationPath(RunMode mode) =>
        mode == RunMode.Relay ? paths.RelayConfigurationPath : paths.ConfigurationPath;

    private static string ModeValue(RunMode mode) => mode == RunMode.Relay ? "relay" : "client";

    private void RestoreRuntimeOwnership()
    {
        if (!OperatingSystem.IsLinux() || GetEffectiveUserId() != 0 ||
            !uint.TryParse(Environment.GetEnvironmentVariable("PKEXEC_UID"), out var userId))
            return;

        var pathsToRestore = new[]
        {
            paths.RuntimeDirectory,
            paths.RuntimeConfigurationPath,
            paths.RunModePath,
            paths.PidPath,
            paths.StandardOutputLogPath,
            paths.StandardErrorLogPath,
        };
        foreach (var path in pathsToRestore.Where(File.Exists).Concat(pathsToRestore.Where(Directory.Exists)))
        {
            if (ChangeSymbolicLinkOwner(path, userId, uint.MaxValue) != 0)
                throw new IOException($"无法恢复运行文件所有权：{path}", new Win32Exception(Marshal.GetLastPInvokeError()));
        }
    }

    [DllImport("shell32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsUserAnAdmin();

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    [DllImport("libc", EntryPoint = "lchown", SetLastError = true)]
    private static extern int ChangeSymbolicLinkOwner(string path, uint owner, uint group);
}
