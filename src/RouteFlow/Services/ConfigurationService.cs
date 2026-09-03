using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RouteFlow.Models;

namespace RouteFlow.Services;

public sealed class ConfigurationService(AppPaths paths, SingBoxProcessService processService)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private JsonObject? _loadedRoot;

    public async Task<ClientConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(paths.ConfigurationPath))
            throw new FileNotFoundException("找不到 config.json。", paths.ConfigurationPath);

        var json = await File.ReadAllTextAsync(paths.ConfigurationPath, Encoding.UTF8, cancellationToken);
        _loadedRoot = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidDataException("config.json 的根节点必须是 JSON 对象。");
        var sections = ResolveSections(_loadedRoot);
        return new ClientConfiguration(
            _loadedRoot,
            GetText(sections.HttpOutbound, "server"),
            GetText(sections.HttpOutbound, "server_port"),
            GetText(sections.HttpOutbound, "username"),
            GetText(sections.HttpOutbound, "password"),
            GetText(sections.DnsServer, "server"),
            GetText(sections.DnsServer, "server_port"),
            GetText(sections.DnsTls, "server_name"),
            GetText(sections.TunInbound, "interface_name"),
            JoinLines(sections.TunInbound["address"] as JsonArray),
            GetBoolean(sections.TunInbound, "auto_route"),
            GetBoolean(sections.TunInbound, "strict_route"),
            JoinLines(sections.TunInbound["route_address"] as JsonArray),
            JoinLines(sections.DomainRule["domain"] as JsonArray));
    }

    public async Task SaveAsync(ClientConfigurationInput input, CancellationToken cancellationToken = default)
    {
        if (_loadedRoot is null)
            throw new InvalidOperationException("配置尚未加载。");

        var candidate = _loadedRoot.DeepClone() as JsonObject
            ?? throw new InvalidDataException("无法复制当前配置。");
        var sections = ResolveSections(candidate);
        sections.HttpOutbound["server"] = RequireText(input.ProxyServer, "HTTP 上游地址");
        sections.HttpOutbound["server_port"] = ParsePort(input.ProxyPort, "HTTP 上游端口");
        ApplyCredentials(sections.HttpOutbound, input.ProxyUsername, input.ProxyPassword);
        sections.DnsServer["server"] = RequireText(input.DnsServer, "DNS over TLS 地址");
        if (string.IsNullOrWhiteSpace(input.DnsPort))
            sections.DnsServer.Remove("server_port");
        else
            sections.DnsServer["server_port"] = ParsePort(input.DnsPort, "DNS 端口");
        sections.DnsTls["server_name"] = RequireText(input.DnsTlsName, "DNS TLS Server Name");
        sections.TunInbound["interface_name"] = RequireText(input.TunInterface, "TUN 网卡名称");
        sections.TunInbound["address"] = ParseLines(input.TunAddresses, "TUN 地址", false);
        sections.TunInbound["auto_route"] = input.AutoRoute;
        sections.TunInbound["strict_route"] = input.StrictRoute;

        var routeAddresses = ParseLines(input.RouteAddresses, "IP/CIDR", false);
        sections.TunInbound["route_address"] = routeAddresses.DeepClone();
        sections.IpRule["ip_cidr"] = routeAddresses;
        sections.DomainRule["domain"] = ParseLines(input.RouteDomains, "域名", true);

        var directory = Path.GetDirectoryName(paths.ConfigurationPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".config-{Guid.NewGuid():N}.json.tmp");
        try
        {
            var json = candidate.ToJsonString(JsonOptions) + Environment.NewLine;
            await File.WriteAllTextAsync(temporaryPath, json, new UTF8Encoding(false), cancellationToken);
            var check = await processService.CheckConfigurationAsync(temporaryPath, cancellationToken);
            if (!check.Success)
                throw new InvalidDataException("sing-box 配置校验失败：" + Environment.NewLine + check.Message);
            File.Move(temporaryPath, paths.ConfigurationPath, true);
            _loadedRoot = candidate;
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static ConfigurationSections ResolveSections(JsonObject root)
    {
        var dns = RequireObject(root, "dns", "缺少 dns 配置。");
        var dnsServer = FindByType(RequireArray(dns, "servers", "缺少 dns.servers。"), "tls")
            ?? throw new InvalidDataException("dns.servers 中找不到 type=tls 的服务器。");
        var dnsTls = RequireObject(dnsServer, "tls", "DNS TLS 配置缺少 tls 对象。");
        var tunInbound = FindByType(RequireArray(root, "inbounds", "缺少 inbounds 配置。"), "tun")
            ?? throw new InvalidDataException("inbounds 中找不到 type=tun 的入站。");
        var httpOutbound = FindByType(RequireArray(root, "outbounds", "缺少 outbounds 配置。"), "http")
            ?? throw new InvalidDataException("outbounds 中找不到 type=http 的上游代理。");
        var route = RequireObject(root, "route", "缺少 route 配置。");
        var rules = RequireArray(route, "rules", "缺少 route.rules 配置。");
        var domainRule = FindRuleWithKey(rules, "domain")
            ?? throw new InvalidDataException("route.rules 中找不到 domain 规则。");
        var ipRule = FindRuleWithKey(rules, "ip_cidr")
            ?? throw new InvalidDataException("route.rules 中找不到 ip_cidr 规则。");
        return new ConfigurationSections(dnsServer, dnsTls, tunInbound, httpOutbound, domainRule, ipRule);
    }

    private static JsonObject RequireObject(JsonObject parent, string key, string error) =>
        parent[key] as JsonObject ?? throw new InvalidDataException(error);

    private static JsonArray RequireArray(JsonObject parent, string key, string error) =>
        parent[key] as JsonArray ?? throw new InvalidDataException(error);

    private static JsonObject? FindByType(JsonArray array, string type) =>
        array.OfType<JsonObject>().FirstOrDefault(item =>
            string.Equals(GetText(item, "type"), type, StringComparison.OrdinalIgnoreCase));

    private static JsonObject? FindRuleWithKey(JsonArray array, string key) =>
        array.OfType<JsonObject>().FirstOrDefault(item => item.ContainsKey(key));

    private static string GetText(JsonObject value, string key) => value[key]?.ToString() ?? string.Empty;

    private static bool GetBoolean(JsonObject value, string key) =>
        value[key] is JsonValue node && node.TryGetValue<bool>(out var result) && result;

    private static string JoinLines(JsonArray? values) => values is null
        ? string.Empty
        : string.Join(Environment.NewLine, values.Select(value => value?.ToString()).Where(value => value is not null));

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

    private static void ApplyCredentials(JsonObject outbound, string usernameValue, string password)
    {
        var username = usernameValue.Trim();
        if ((username.Length == 0) != (password.Length == 0))
            throw new InvalidDataException("上游代理用户名和密码必须同时填写，或同时留空。");
        if (username.Length == 0)
        {
            outbound.Remove("username");
            outbound.Remove("password");
            return;
        }
        outbound["username"] = username;
        outbound["password"] = password;
    }

    private static JsonArray ParseLines(string text, string displayName, bool allowEmpty)
    {
        var values = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Split('\n')
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!allowEmpty && values.Length == 0)
            throw new InvalidDataException($"{displayName}至少需要填写一项。");
        return new JsonArray(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
    }

    private sealed record ConfigurationSections(
        JsonObject DnsServer,
        JsonObject DnsTls,
        JsonObject TunInbound,
        JsonObject HttpOutbound,
        JsonObject DomainRule,
        JsonObject IpRule);
}
