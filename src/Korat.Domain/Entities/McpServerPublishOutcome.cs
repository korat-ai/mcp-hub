namespace Korat.Domain.Entities;

/// <summary>
/// Р26/Р27: what a re-publish did, beyond returning the server.
///
/// <para>Publishing is idempotent on <c>(SpaceId, DisplayName)</c> and returns the same
/// <see cref="McpServerId"/> when the same publisher node re-publishes. That stability is
/// deliberate — the daemon builds its routing table from the ack — but it means a re-publish can
/// change WHAT runs behind an already-approved name. When that happens the permissions for the
/// server are suspended and this record carries everything the caller needs to finish the job:
/// terminate the live sessions, and tell the owner what changed.</para>
///
/// <para>Null <see cref="Redefinition"/> is the overwhelmingly common case: a daemon reconnecting
/// and re-declaring the same definition. Nothing is suspended and nothing is reported.</para>
/// </summary>
public sealed record McpServerPublishOutcome(
    McpServer? Server,
    McpServerRedefinition? Redefinition);

/// <summary>
/// Р26/Р27: an already-published server whose launch definition changed under the same name and
/// the same publisher node.
///
/// <para>The before/after command pair is carried explicitly rather than left for the caller to
/// reconstruct, because the owner-facing notification must show a diff. A notification that only
/// says "the definition changed" is worse than none: it invites a reflexive approve, which is
/// exactly how Р26's protection gets bypassed through the human.</para>
/// </summary>
public sealed record McpServerRedefinition(
    McpServerId ServerId,
    SpaceId SpaceId,
    string DisplayName,
    string PreviousCommand,
    string PreviousArguments,
    string NewCommand,
    string NewArguments,
    string PreviousDigest,
    string NewDigest,
    IReadOnlyList<GrantId> SuspendedGrantIds,
    IReadOnlyList<SessionId> SessionsToTerminate);

/// <summary>
/// Р26/Р27: the result of a declarative re-sync — the servers it asserted, plus every redefinition
/// it performed along the way. Redefinitions are collected rather than summarised because each one
/// carries its own before/after pair and its own set of sessions to terminate.
/// </summary>
public sealed record McpServerSyncOutcome(
    IReadOnlyList<McpServer> Servers,
    IReadOnlyList<McpServerRedefinition> Redefinitions);
