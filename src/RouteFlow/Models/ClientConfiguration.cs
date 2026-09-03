using System.Text.Json.Nodes;

namespace RouteFlow.Models;

public sealed record ClientConfiguration(
    JsonObject Root,
    string ProxyServer,
    string ProxyPort,
    string ProxyUsername,
    string ProxyPassword,
    string DnsServer,
    string DnsPort,
    string DnsTlsName,
    string TunInterface,
    string TunAddresses,
    bool AutoRoute,
    bool StrictRoute,
    string RouteAddresses,
    string RouteDomains);

public sealed record ClientConfigurationInput(
    string ProxyServer,
    string ProxyPort,
    string ProxyUsername,
    string ProxyPassword,
    string DnsServer,
    string DnsPort,
    string DnsTlsName,
    string TunInterface,
    string TunAddresses,
    bool AutoRoute,
    bool StrictRoute,
    string RouteAddresses,
    string RouteDomains);

public enum RunMode
{
    Client,
    Relay,
}

public sealed record RelayConfiguration(
    string ListenAddress,
    string ListenPort,
    string ClientAddress,
    string Username,
    string Password);

public sealed record RelayConfigurationInput(
    string ListenAddress,
    string ListenPort,
    string ClientAddress,
    string Username,
    string Password);

public sealed record ProcessStatus(bool IsRunning, int? ProcessId, RunMode? Mode);

public sealed record ConnectionTestResult(bool Success, long ElapsedMilliseconds, string Message);
