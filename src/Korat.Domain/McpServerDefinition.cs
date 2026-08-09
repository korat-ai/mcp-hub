using System.Security.Cryptography;
using System.Text;
using Korat.Domain.Entities;

namespace Korat.Domain;

/// <summary>
/// Р26: the digest of what an owner actually approved.
///
/// <para>A permission (<see cref="Grant"/>) used to be keyed on <c>(ConsumerId, McpServerId)</c>
/// alone. That is not what the owner agreed to: they approved a named server that runs a specific
/// command (or points at a specific URL). Because
/// <c>SpaceGrain.PublishMcpServerAsync</c> is idempotent on <c>(SpaceId, DisplayName)</c> and
/// returns the SAME <c>McpServerId</c> when the same publisher node re-publishes, changing the
/// launch definition kept every existing permission attached to the new definition. Approval was
/// inherited rather than requested.</para>
///
/// <para>Binding the permission to this digest turns that into the SSH <c>known_hosts</c> /
/// Chrome-extension behaviour: when the thing behind the name changes, the permission stops
/// applying until the owner approves the change.</para>
///
/// <para><b>What the digest deliberately covers:</b> everything that determines what code answers
/// a relayed request — transport, the stdio command and its arguments, the remote URL for HTTP
/// servers, the auth mode, and the custom auth header name (SSRF-relevant, injected verbatim by
/// the proxy).</para>
///
/// <para><b>What it deliberately does not cover:</b> <c>DisplayName</c> (renaming is not a
/// capability change and re-prompting on it would train owners to click through),
/// <c>Status</c>/<c>IsAsserted</c>/<c>LastSeenAt</c> (lifecycle, not identity), and the stored
/// secret (rotating a credential for the same endpoint is not a change of what is being called).
/// </para>
///
/// <para><b>What it cannot cover at all</b> — see docs/security/threat-model.md, "Not protected"
/// §2: the digest pins the command, not the program. Whoever can write to the publisher machine
/// can replace the binary that command resolves to, and this digest still matches.</para>
/// </summary>
public static class McpServerDefinition
{
    /// <summary>Length of the hex digest kept on the entity. 32 hex chars = 128 bits.</summary>
    public const int DigestHexLength = 32;

    /// <summary>
    /// Stable digest of <paramref name="server"/>'s definition. Field values are joined with the
    /// ASCII unit separator (U+001F), which cannot occur in any of them, so that
    /// <c>("ab", "c")</c> and <c>("a", "bc")</c> cannot collide.
    /// </summary>
    public static string Digest(McpServer server) => Digest(
        server.Transport,
        server.LaunchCommand,
        server.LaunchArguments,
        server.RemoteUrl,
        server.AuthMode,
        server.AuthHeaderName);

    /// <summary>
    /// Field-wise overload — used by call sites that hold the parts before an entity exists
    /// (e.g. the publish path deciding whether an incoming definition differs from the stored one).
    /// </summary>
    public static string Digest(
        string transport,
        string launchCommand,
        string launchArguments,
        string? remoteUrl,
        string? authMode,
        string? authHeaderName)
    {
        var input = string.Join('\u001f',
            transport ?? string.Empty,
            launchCommand ?? string.Empty,
            launchArguments ?? string.Empty,
            remoteUrl ?? string.Empty,
            authMode ?? string.Empty,
            authHeaderName ?? string.Empty);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..DigestHexLength].ToLowerInvariant();
    }
}
