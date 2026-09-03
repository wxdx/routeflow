using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RouteFlow.Models;

namespace RouteFlow.Services;

public sealed class SingBoxProcessService(AppPaths paths)
{
    private Process? _ownedProcess;

    public async Task<(bool Success, string Message)> CheckConfigurationAsync(
        string configurationPath,
        CancellationToken cancellationToken = default)
    {
        EnsureBinaryExists();
        using var process = new Process { StartInfo = CreateStartInfo("check", "-c", configurationPath) };
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var message = string.Join(Environment.NewLine, await outputTask, await errorTask).Trim();
        if (message.Length == 0 && process.ExitCode != 0)
            message = $"sing-box 退出码：{process.ExitCode}";
        return (process.ExitCode == 0, message);
    }

    public Task<ProcessStatus> GetStatusAsync()
    {
        var process = FindManagedProcess();
        return Task.FromResult(new ProcessStatus(
            process is not null,
            process?.Id,
            process is null ? null : ReadRunMode()));
    }

    public async Task<int> StartAsync(
        string configurationPath,
        RunMode mode,
        CancellationToken cancellationToken = default)
    {
        var existing = FindManagedProcess();
        if (existing is not null)
            return existing.Id;

        Directory.CreateDirectory(paths.RuntimeDirectory);
        await File.WriteAllTextAsync(paths.StandardOutputLogPath, string.Empty, new UTF8Encoding(false), cancellationToken);
        await File.WriteAllTextAsync(paths.StandardErrorLogPath, string.Empty, new UTF8Encoding(false), cancellationToken);
        var runtimeConfigurationPath = await WriteRuntimeConfigurationAsync(configurationPath, cancellationToken);
        var check = await CheckConfigurationAsync(runtimeConfigurationPath, cancellationToken);
        if (!check.Success)
            throw new InvalidDataException("sing-box 配置校验失败：" + Environment.NewLine + check.Message);

        var process = new Process
        {
            StartInfo = CreateStartInfo("--disable-color", "run", "-c", runtimeConfigurationPath),
            EnableRaisingEvents = true,
        };
        process.Start();
        _ownedProcess = process;

        await Task.Delay(800, cancellationToken);
        if (process.HasExited)
            throw new InvalidOperationException("sing-box 启动后立即退出。" + Environment.NewLine + ReadLogTail(paths.StandardOutputLogPath));

        await File.WriteAllTextAsync(paths.PidPath, process.Id.ToString(), Encoding.ASCII, cancellationToken);
        await File.WriteAllTextAsync(paths.RunModePath, ModeValue(mode), Encoding.ASCII, cancellationToken);
        return process.Id;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var process = FindManagedProcess();
        if (process is null)
        {
            DeletePidFile();
            DeleteRunModeFile();
            return;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            process.Kill(true);
        }
        else
        {
            using var terminator = Process.Start(new ProcessStartInfo
            {
                FileName = "/bin/kill",
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "-TERM", process.Id.ToString() },
            });
            if (terminator is not null)
                await terminator.WaitForExitAsync(cancellationToken);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(true);
            await process.WaitForExitAsync(cancellationToken);
        }

        DeletePidFile();
        DeleteRunModeFile();
        _ownedProcess = null;
    }

    public async Task<ConnectionTestResult> TestTcpAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeout.Token);
            stopwatch.Stop();
            return new ConnectionTestResult(true, stopwatch.ElapsedMilliseconds, "连接成功");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ConnectionTestResult(false, stopwatch.ElapsedMilliseconds, "连接超时");
        }
        catch (Exception exception)
        {
            return new ConnectionTestResult(false, stopwatch.ElapsedMilliseconds, exception.Message);
        }
    }

    private ProcessStartInfo CreateStartInfo(params string[] arguments)
    {
        EnsureBinaryExists();
        var startInfo = new ProcessStartInfo
        {
            FileName = paths.SingBoxPath,
            WorkingDirectory = paths.HomeDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    private Process? FindManagedProcess()
    {
        if (_ownedProcess is { HasExited: false })
            return _ownedProcess;
        if (!File.Exists(paths.PidPath) || !int.TryParse(File.ReadAllText(paths.PidPath).Trim(), out var processId))
            return null;

        try
        {
            var process = Process.GetProcessById(processId);
            if (IsExpectedProcess(process))
                return process;
            process.Dispose();
        }
        catch
        {
            // A stale PID file is removed below.
        }

        DeletePidFile();
        return null;
    }

    private bool IsExpectedProcess(Process process)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var link = new FileInfo($"/proc/{process.Id}/exe").ResolveLinkTarget(true);
                return link is not null && PathsEqual(link.FullName, paths.SingBoxPath);
            }
            try
            {
                return process.MainModule?.FileName is { } executablePath && PathsEqual(executablePath, paths.SingBoxPath);
            }
            catch when (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return string.Equals(process.ProcessName, "sing-box", StringComparison.OrdinalIgnoreCase) &&
                    File.GetLastWriteTimeUtc(paths.PidPath) >= process.StartTime.ToUniversalTime();
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.GetFullPath(left),
        Path.GetFullPath(right),
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private async Task<string> WriteRuntimeConfigurationAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(sourcePath, Encoding.UTF8, cancellationToken);
        var root = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidDataException("config.json 的根节点必须是 JSON 对象。");
        var log = root["log"] as JsonObject;
        if (log is null)
        {
            log = new JsonObject();
            root["log"] = log;
        }
        log["output"] = paths.StandardOutputLogPath;
        await File.WriteAllTextAsync(
            paths.RuntimeConfigurationPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
            new UTF8Encoding(false),
            cancellationToken);
        return paths.RuntimeConfigurationPath;
    }

    private static string ReadLogTail(string path) => File.Exists(path)
        ? string.Join(Environment.NewLine, File.ReadLines(path).TakeLast(25))
        : "没有生成错误日志。";

    private void EnsureBinaryExists()
    {
        if (!File.Exists(paths.SingBoxPath))
            throw new FileNotFoundException("找不到 sing-box 可执行文件。", paths.SingBoxPath);
    }

    private void DeletePidFile()
    {
        if (File.Exists(paths.PidPath))
            File.Delete(paths.PidPath);
    }

    private RunMode ReadRunMode()
    {
        try
        {
            return File.Exists(paths.RunModePath) &&
                string.Equals(File.ReadAllText(paths.RunModePath).Trim(), "relay", StringComparison.OrdinalIgnoreCase)
                ? RunMode.Relay
                : RunMode.Client;
        }
        catch
        {
            return RunMode.Client;
        }
    }

    private void DeleteRunModeFile()
    {
        if (File.Exists(paths.RunModePath))
            File.Delete(paths.RunModePath);
    }

    private static string ModeValue(RunMode mode) => mode == RunMode.Relay ? "relay" : "client";
}
