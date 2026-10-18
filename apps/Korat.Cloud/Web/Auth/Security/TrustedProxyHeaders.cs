using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.HttpOverrides;

namespace Korat.Cloud.Web.Auth.Security;

/// <summary>One trust boundary for the request scheme and client IP, independent of the host.</summary>
public static class TrustedProxyHeaders
{
    public static bool AddKoratForwardedHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        // The framework's environment switch installs a second, unrestricted middleware.
        // It must not silently override this application's explicit allowlist.
        if (configuration.GetValue<bool>("ForwardedHeaders_Enabled")
            || configuration.GetValue<bool>("ASPNETCORE_FORWARDEDHEADERS_ENABLED")
            || configuration.GetValue<bool>("DOTNET_FORWARDEDHEADERS_ENABLED"))
            throw new InvalidOperationException(
                "Use Korat:Cloud:TrustForwardedIp and Korat:Cloud:TrustedProxies instead of the unrestricted forwarded-headers switch.");

        if (!configuration.GetValue<bool>("Korat:Cloud:TrustForwardedIp"))
            return false;

        var section = configuration.GetSection("Korat:Cloud:TrustedProxies");
        var entries = (section.Value is { } value
                ? value.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                : section.Get<string[]>() ?? [])
            .Select(value => value?.Trim() ?? string.Empty).ToArray();
        if (entries.Length == 0)
            throw new InvalidOperationException(
                "Korat:Cloud:TrustedProxies must contain explicit proxy IP addresses or CIDR networks when TrustForwardedIp is enabled.");

        var addresses = new List<IPAddress>();
        var networks = new List<System.Net.IPNetwork>();
        foreach (var entry in entries)
        {
            if (IPAddress.TryParse(entry, out var address))
            {
                addresses.Add(address);
                if (address.AddressFamily == AddressFamily.InterNetwork)
                    addresses.Add(address.MapToIPv6());
            }
            else if (System.Net.IPNetwork.TryParse(entry, out var network)
                && network.PrefixLength > 0
                && !(network.BaseAddress.IsIPv4MappedToIPv6 && network.PrefixLength <= 96))
            {
                networks.Add(network);
                if (network.BaseAddress.AddressFamily == AddressFamily.InterNetwork)
                    networks.Add(new System.Net.IPNetwork(network.BaseAddress.MapToIPv6(), 96 + network.PrefixLength));
            }
            else
            {
                throw new InvalidOperationException(
                    $"Invalid Korat:Cloud:TrustedProxies entry '{entry}'. Use an explicit IP address or bounded CIDR network, not a wildcard.");
            }
        }

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (var address in addresses) options.KnownProxies.Add(address);
            foreach (var network in networks) options.KnownIPNetworks.Add(network);
        });
        return true;
    }
}
