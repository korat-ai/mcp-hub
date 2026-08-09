using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;

namespace Korat.Cloud.Web.Spaces;

/// <summary>
/// SR-T3-5: DNS resolver seam used by <see cref="SsrfGuardedHttpClientFactory"/>.
/// In production, resolves via <see cref="Dns.GetHostAddressesAsync"/>.
/// In tests, replaced with a stub that injects controlled IP addresses.
/// </summary>
public interface ISsrfDnsResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct = default);
}

/// <summary>Default production implementation: delegates to system DNS.</summary>
public sealed class SystemSsrfDnsResolver : ISsrfDnsResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct = default) =>
        Dns.GetHostAddressesAsync(host, ct);
}

/// <summary>
/// SR-T3-5: SSRF validation and blocked-address detection.
/// Validates a user-supplied URL at registration time.
/// The ConnectCallback in <see cref="SsrfGuardedHttpClientFactory"/> re-validates at connect time
/// (resolves → checks → pins) to defeat DNS rebinding.
/// </summary>
public static class SsrfGuard
{
    // Maximum URL length accepted at registration.
    private const int MaxUrlLength = 2048;

    // Allowed destination ports. 443 = standard HTTPS, 8443 = common alternative HTTPS.
    private static readonly HashSet<int> AllowedPorts = [443, 8443];

    /// <summary>
    /// Validates a user-supplied URL at registration time.
    /// Returns null on success; an error message on failure.
    /// Does NOT resolve the hostname — call the connect-time guard for that.
    /// </summary>
    public static string? ValidateUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "URL must not be empty.";

        if (url.Length > MaxUrlLength)
            return $"URL exceeds maximum length of {MaxUrlLength} characters.";

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "URL is not a valid absolute URI.";

        if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            return "Only HTTPS URLs are allowed.";

        if (!string.IsNullOrEmpty(uri.UserInfo))
            return "URLs with userinfo (username:password) are not allowed.";

        // SECURITY MINOR-3: restrict destination port to 443 and 8443.
        // uri.Port returns -1 when no port is specified in the URL (default port for scheme).
        var port = uri.Port == -1 ? 443 : uri.Port;
        if (!AllowedPorts.Contains(port))
            return $"Only port 443 (and 8443) are allowed for outbound URLs; got port {port}.";

        // If the host is a literal IP address, validate it immediately.
        if (IPAddress.TryParse(uri.Host, out var literalIp))
        {
            if (IsBlockedAddress(literalIp))
                return $"The IP address {literalIp} is in a blocked range.";
        }

        return null;
    }

    /// <summary>
    /// Returns true if the resolved IP address must be blocked (private/loopback/link-local/metadata/etc.).
    /// Called at connect time for every resolved address.
    /// </summary>
    public static bool IsBlockedAddress(IPAddress address)
    {
        // Normalise IPv4-mapped IPv6 (::ffff:a.b.c.d → a.b.c.d) so the IPv4 rules apply.
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
            return IsBlockedIpv4(address);

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return IsBlockedIpv6(address);

        // Unknown family → block by default.
        return true;
    }

    // ── IPv4 block ranges ──────────────────────────────────────────────────────
    // Blocked: 0/8 (this network), 10/8, 100.64/10 (CGNAT), 127/8 (loopback),
    //          169.254/16 (link-local + cloud metadata), 172.16/12, 192.0.0/24,
    //          192.168/16, 198.18/15 (benchmarking), 224/4 (multicast), 240/4 (reserved),
    //          255.255.255.255 (broadcast).
    private static bool IsBlockedIpv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        int b0 = bytes[0], b1 = bytes[1], b2 = bytes[2];

        return b0 == 0                                          // 0.0.0.0/8
            || b0 == 10                                         // 10.0.0.0/8
            || (b0 == 100 && b1 >= 64 && b1 <= 127)            // 100.64.0.0/10 (CGNAT)
            || b0 == 127                                        // 127.0.0.0/8 (loopback)
            || (b0 == 169 && b1 == 254)                        // 169.254.0.0/16 (link-local + metadata)
            || (b0 == 172 && b1 >= 16 && b1 <= 31)            // 172.16.0.0/12
            || (b0 == 192 && b1 == 0 && b2 == 0)              // 192.0.0.0/24 (IETF protocol)
            || (b0 == 192 && b1 == 168)                        // 192.168.0.0/16
            || (b0 == 198 && (b1 == 18 || b1 == 19))          // 198.18.0.0/15 (benchmarking)
            || b0 >= 224;                                       // 224/4 multicast + 240/4 reserved + 255.255.255.255
    }

    // ── IPv6 block ranges ──────────────────────────────────────────────────────
    // Blocked: :: (unspecified), ::1 (loopback), fc00::/7 (ULA), fe80::/10 (link-local),
    //          ff00::/8 (multicast), 64:ff9b::/96 (NAT64 — unwrap to IPv4 and re-check),
    //          ::ffff:0:0/96 (IPv4-mapped — normalised above; blocked here as extra guard).
    private static bool IsBlockedIpv6(IPAddress address)
    {
        var bytes = address.GetAddressBytes();

        // Unspecified (::)
        if (address.Equals(IPAddress.IPv6Any))
            return true;

        // Loopback (::1)
        if (address.Equals(IPAddress.IPv6Loopback))
            return true;

        int b0 = bytes[0], b1 = bytes[1];

        // fc00::/7 (ULA) → b0 in [0xfc, 0xfd]
        if ((b0 & 0xFE) == 0xFC)
            return true;

        // fe80::/10 (link-local) → b0==0xFE, b1 in [0x80..0xBF]
        if (b0 == 0xFE && (b1 & 0xC0) == 0x80)
            return true;

        // ff00::/8 (multicast)
        if (b0 == 0xFF)
            return true;

        // ::ffff:0:0/96 (IPv4-mapped) — should have been normalised above, but block here too
        if (bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0 && bytes[3] == 0 &&
            bytes[4] == 0 && bytes[5] == 0 && bytes[6] == 0 && bytes[7] == 0 &&
            bytes[8] == 0 && bytes[9] == 0 && bytes[10] == 0xFF && bytes[11] == 0xFF)
            return true;

        // 64:ff9b::/96 (NAT64) — unwrap the embedded IPv4 and re-check
        if (bytes[0] == 0x00 && bytes[1] == 0x64 &&
            bytes[2] == 0xFF && bytes[3] == 0x9B)
        {
            var v4 = new IPAddress(bytes[12..16]);
            return IsBlockedIpv4(v4);
        }

        // SECURITY MINOR-1: IPv4-compatible IPv6 (deprecated RFC 4291 §2.5.5.1).
        // Format: ::a.b.c.d — high 96 bits are all zero, low 32 bits carry the IPv4 address.
        // These are distinct from ::ffff:a.b.c.d (IPv4-mapped, handled above via IsIPv4MappedToIPv6).
        // The check: bytes[0..11] all zero, but NOT :: (all zeros) and NOT ::1 (loopback, already handled).
        // We re-run IsBlockedIpv4 on the embedded address in bytes[12..16].
        if (bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0 && bytes[3] == 0 &&
            bytes[4] == 0 && bytes[5] == 0 && bytes[6] == 0 && bytes[7] == 0 &&
            bytes[8] == 0 && bytes[9] == 0 && bytes[10] == 0 && bytes[11] == 0)
        {
            // bytes[12..16] is the embedded IPv4 address (could be 0.0.0.0 = ::, or 0.0.0.1 = ::1).
            var embedded = new IPAddress(bytes[12..16]);
            // :: (0.0.0.0) and ::1 (0.0.0.1) are already blocked above; re-check here is harmless
            // but we must also block any other embedded private address (e.g. ::127.0.0.1, ::10.0.0.1).
            return IsBlockedIpv4(embedded);
        }

        return false;
    }
}

/// <summary>
/// SR-T3-5: creates <see cref="HttpClient"/> instances whose outbound connections are SSRF-guarded.
/// Uses a <see cref="SocketsHttpHandler.ConnectCallback"/> that:
///   1. Resolves the host via <see cref="ISsrfDnsResolver"/> at connect time (defeats DNS rebinding).
///   2. Rejects any address in a blocked range.
///   3. Pins the connection to the first passing resolved address.
/// Auto-redirect is disabled (3xx treated as 502 at the call site).
/// </summary>
public sealed class SsrfGuardedHttpClientFactory(
    ISsrfDnsResolver dnsResolver,
    IConfiguration configuration,
    IHostEnvironment hostEnvironment,
    ILogger<SsrfGuardedHttpClientFactory> logger) : Korat.Domain.IOutboundHttpClientFactory
{
    // SECURITY MAJOR-2: AllowPrivateNetworks is ONLY honoured in Development/Testing environments.
    // In any other environment the flag is silently ignored and SSRF checks always run.
    // This prevents a misconfigured prod setting from opening a full SSRF escape hatch.
    private bool AllowPrivateNetworks
    {
        get
        {
            var flagValue = configuration.GetValue<bool>("Korat:Inference:Outbound:AllowPrivateNetworks");
            if (!flagValue)
                return false;

            // Only honour the flag in development/testing environments.
            if (hostEnvironment.IsDevelopment() ||
                string.Equals(hostEnvironment.EnvironmentName, "Testing", StringComparison.OrdinalIgnoreCase))
                return true;

            // Flag is set but we are NOT in a development environment — log a loud warning and ignore.
            logger.LogWarning(
                "SSRF guard: Korat:Inference:Outbound:AllowPrivateNetworks=true is set but the current " +
                "environment is '{Environment}' (not Development/Testing). The flag is IGNORED. " +
                "SSRF checks remain active. Do not set this flag in production.",
                hostEnvironment.EnvironmentName);
            return false;
        }
    }

    public HttpClient CreateClient(string purposeLabel)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect          = false,
            UseProxy                   = false,
            UseCookies                 = false,
            ConnectTimeout             = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime   = TimeSpan.FromMinutes(5),
            ConnectCallback            = async (ctx, ct) =>
            {
                if (AllowPrivateNetworks)
                {
                    // Test/Development mode: connect normally without SSRF checks.
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    await socket.ConnectAsync(ctx.DnsEndPoint, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }

                var host = ctx.DnsEndPoint.Host;
                IPAddress[] addresses;
                try
                {
                    addresses = await dnsResolver.ResolveAsync(host, ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "SSRF guard: DNS resolution failed for host '{Host}'", host);
                    throw new HttpRequestException($"SSRF: DNS resolution failed for '{host}'.");
                }

                if (addresses.Length == 0)
                    throw new HttpRequestException($"SSRF: no addresses resolved for '{host}'.");

                // All resolved addresses must be public — block if ANY is private (mixed DNS attack).
                foreach (var addr in addresses)
                {
                    if (SsrfGuard.IsBlockedAddress(addr))
                    {
                        logger.LogWarning(
                            "SSRF guard: blocked address {Address} resolved for '{Host}' in {Purpose}",
                            addr, host, purposeLabel);
                        throw new HttpRequestException($"SSRF: address {addr} is in a blocked range.");
                    }
                }

                // Pin to first passing address — connect socket directly (no further DNS).
                var target = addresses[0];
                var endpoint = new IPEndPoint(target, ctx.DnsEndPoint.Port);
                var sock = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true
                };
                try
                {
                    await sock.ConnectAsync(endpoint, ct);
                    return new NetworkStream(sock, ownsSocket: true);
                }
                catch
                {
                    sock.Dispose();
                    throw;
                }
            }
        };

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(600), // total; per-request caps enforced in OutboundInferenceClient
        };
    }
}
