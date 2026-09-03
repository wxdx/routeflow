using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RouteFlow.Models;

namespace RouteFlow.Services;

public sealed class RelayConfigurationService(AppPaths paths, SingBoxProcessService processService)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<RelayConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        var defaults = new RelayConfiguration("0.0.0.0", "7890", "192.168.63.63", string.Empty, string.Empty);
        if (!File.Exists(paths.RelaySettingsPath))
            return defaults;

        var json = await File.ReadAllTextAsync(paths.RelaySettingsPath, Encoding.UTF8, cancellationToken);
        var root = JsonNode.Parse(json) as JsonObject;
        var relay = root?["relay"] as JsonObject;
        if (relay is null)
            return defaults;

        return new RelayConfiguration(
            TextOrDefault(relay, "listen", defaults.ListenAddress),
            TextOrDefault(relay, "port", defaults.ListenPort),
            TextOrDefault(relay, "client_address", defaults.ClientAddress),
            TextOrDefault(relay, "username", defaults.Username),
            TextOrDefault(relay, "password", defaults.Password));
    }

    public async Task SaveAsync(RelayConfigurationInput input, CancellationToken cancellationToken = default)
    {
        var listenAddress = RequireText(input.ListenAddress, "中转监听地址");
        var listenPort = ParsePort(input.ListenPort, "中转监听端口");
        var clientAddress = RequireText(input.ClientAddress, "客户端连接地址");
        var username = input.Username.Trim();
        if ((username.Length == 0) != (input.Password.Length == 0))
            throw new InvalidDataException("中转认证的用户名和密码必须同时填写，或同时留空。");

        var relayConfiguration = BuildRelayConfiguration(listenAddress, listenPort, username, input.Password);
        var settings = BuildSettings(listenAddress, listenPort, clientAddress, username, input.Password);
        var relayTempPath = paths.RelayConfigurationPath + $".{Guid.NewGuid():N}.tmp";
        var settingsTempPath = paths.RelaySettingsPath + $".{Guid.NewGuid():N}.tmp";

        try
        {
            await WriteJsonAsync(relayTempPath, relayConfiguration, cancellationToken);
            var check = await processService.CheckConfigurationAsync(relayTempPath, cancellationToken);
            if (!check.Success)
                throw new InvalidDataException("内网中转配置校验失败：" + Environment.NewLine + check.Message);

            await WriteJsonAsync(settingsTempPath, settings, cancellationToken);
            File.Move(relayTempPath, paths.RelayConfigurationPath, true);
            File.Move(settingsTempPath, paths.RelaySettingsPath, true);
        }
        finally
        {
            DeleteIfExists(relayTempPath);
            DeleteIfExists(settingsTempPath);
        }
    }

    public async Task ValidateDefaultsAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(paths.RuntimeDirectory);
        var temporaryPath = Path.Combine(paths.RuntimeDirectory, $".relay-self-test-{Guid.NewGuid():N}.json.tmp");
        try
        {
            var configuration = BuildRelayConfiguration("127.0.0.1", 7890, string.Empty, string.Empty);
            await WriteJsonAsync(temporaryPath, configuration, cancellationToken);
            var check = await processService.CheckConfigurationAsync(temporaryPath, cancellationToken);
            if (!check.Success)
                throw new InvalidDataException("内网中转自检失败：" + Environment.NewLine + check.Message);
        }
        finally
        {
            DeleteIfExists(temporaryPath);
        }
    }

    private static JsonObject BuildRelayConfiguration(string listenAddress, int listenPort, string username, string password)
    {
        var inbound = new JsonObject
        {
            ["type"] = "mixed",
            ["tag"] = "lan-relay",
            ["listen"] = listenAddress,
            ["listen_port"] = listenPort,
        };
        if (username.Length > 0)
        {
            inbound["users"] = new JsonArray
            {
                new JsonObject
                {
                    ["username"] = username,
                    ["password"] = password,
                },
            };
        }

        return new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "info" },
            ["inbounds"] = new JsonArray { inbound },
            ["outbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "direct",
                    ["tag"] = "direct",
                },
            },
            ["route"] = new JsonObject { ["final"] = "direct" },
        };
    }

    private static JsonObject BuildSettings(
        string listenAddress,
        int listenPort,
        string clientAddress,
        string username,
        string password) => new()
        {
            ["relay"] = new JsonObject
            {
                ["listen"] = listenAddress,
                ["port"] = listenPort.ToString(),
                ["client_address"] = clientAddress,
                ["username"] = username,
                ["password"] = password,
            },
        };

    private static async Task WriteJsonAsync(string path, JsonObject value, CancellationToken cancellationToken) =>
        await File.WriteAllTextAsync(
            path,
            value.ToJsonString(JsonOptions) + Environment.NewLine,
            new UTF8Encoding(false),
            cancellationToken);

    private static string TextOrDefault(JsonObject value, string key, string fallback) =>
        value[key]?.ToString() is { Length: > 0 } result ? result : fallback;

    private static string RequireText(string value, string displayName)
    {
        var result = value.Trim();
        return result.Length > 0 ? result : throw new InvalidDataException($"{displayName}不能为空。");
    }

    private static int ParsePort(string value, string displayName)
    {
        if (!int.TryParse(value.Trim(), out var port) || port is < 1 or > 65535)
            throw new InvalidDataException($"{displayName}必须是 1 到 65535 之间的整数。");
        return port;
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}
