using System.Runtime.InteropServices;

namespace RouteFlow.Models;

public sealed record AppPaths(
    string HomeDirectory,
    string ConfigurationPath,
    string RelaySettingsPath,
    string RelayConfigurationPath,
    string SingBoxPath,
    string RuntimeDirectory,
    string RuntimeConfigurationPath,
    string RunModePath,
    string PidPath,
    string StandardOutputLogPath,
    string StandardErrorLogPath)
{
    public static AppPaths Resolve()
    {
        var binaryName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "sing-box.exe" : "sing-box";
        var candidates = new List<string>();
        var configuredHome = Environment.GetEnvironmentVariable("SING_BOX_HOME");
        if (!string.IsNullOrWhiteSpace(configuredHome))
            candidates.Add(configuredHome);

        candidates.Add(AppContext.BaseDirectory);
        candidates.Add(Environment.CurrentDirectory);
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 4 && directory.Parent is not null; depth++)
        {
            directory = directory.Parent;
            candidates.Add(directory.FullName);
        }

        var resolvedCandidates = candidates
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var home = resolvedCandidates
            .FirstOrDefault(path => File.Exists(Path.Combine(path, "config.json")))
            ?? resolvedCandidates.FirstOrDefault(path => File.Exists(Path.Combine(path, binaryName)))
            ?? Path.GetFullPath(AppContext.BaseDirectory);
        var singBoxPath = ResolveBinaryPath(home, binaryName);
        var runtimeDirectory = Path.Combine(home, "log");
        return new AppPaths(
            home,
            Path.Combine(home, "config.json"),
            Path.Combine(home, "relay-settings.json"),
            Path.Combine(home, "relay-config.json"),
            singBoxPath,
            runtimeDirectory,
            Path.Combine(runtimeDirectory, "runtime-config.json"),
            Path.Combine(runtimeDirectory, "run-mode.txt"),
            Path.Combine(runtimeDirectory, "sing-box.pid"),
            Path.Combine(runtimeDirectory, "sing-box.out.log"),
            Path.Combine(runtimeDirectory, "sing-box.err.log"));
    }

    private static string ResolveBinaryPath(string home, string binaryName)
    {
        var configuredPath = Environment.GetEnvironmentVariable("SING_BOX_PATH");
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return Path.GetFullPath(configuredPath);

        var localPath = Path.Combine(home, binaryName);
        if (File.Exists(localPath))
            return localPath;

        var searchPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in searchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), binaryName);
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
            catch
            {
                // Ignore malformed PATH entries and continue searching.
            }
        }

        return localPath;
    }
}
