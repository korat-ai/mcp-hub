using System.Collections.Concurrent;
using Korat.Domain;
using Korat.Domain.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Korat.Cloud.Push;

/// <summary>
/// 031 (mobile-push increment 2), design §4b: stateless singleton that fans out a push alert to
/// every push-enabled node of a Space when a NEW pending access-request is created. Never called
/// on the idempotent replay path or the dev auto-approve path — the caller (NodeGatewayService,
/// Task 7) only invokes this when CreateAccessRequestWithStatusAsync's Created flag is true.
/// </summary>
public sealed class AccessRequestNotifier(
    IAccessRequestGrainLocator locator,
    IAlertPushSender alertSender,
    IOptions<AccessRequestNotifyOptions> opts,
    ILogger<AccessRequestNotifier> log)
{
    private readonly AccessRequestNotifyOptions _opts = opts.Value;

    // Per-space throttle: SpaceId.Value → last-notify time. This is a STORM safety-valve
    // (§MED-3), not a per-request debounce: a gRPC gateway with no rate limiter + agent-supplied
    // ConsumerId means one node can mint unlimited identities → unlimited new (agent,server)
    // pairs → unlimited pushes in a tight loop. Post-review correction (Fable holistic plan
    // review): the window is intentionally SHORT (default matches ApnsOptions.WakeDedupSeconds,
    // ~10s) because it drops genuinely DISTINCT (agent, server) requests arriving within the
    // window, not just literal duplicates — a long window would suppress legitimate concurrent
    // access requests from different agents. The owner still sees every suppressed request in the
    // in-app pending list; only the PUSH for the 2nd+ distinct request inside the window is
    // dropped.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastNotifyAt = new();

    public async Task NotifyOwnerOfNewRequestAsync(SpaceId spaceId, AccessRequest request, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var throttleWindow = TimeSpan.FromSeconds(_opts.ThrottleSeconds);

        // Atomically claim the window BEFORE any enumeration (holistic-review fix, §Important):
        // the previous check-then-act (TryGetValue, then stamp only after the awaited
        // ListNodesAsync/ListMcpServersAsync) let N concurrent detached notifies for the same
        // space all pass the check before any of them stamped — a burst of distinct (agent,
        // server) requests could leak up to N pushes per device, defeating the storm control this
        // throttle exists for. Only the call that WINS the claim proceeds past this point.
        if (!TryClaimWindow(spaceId.Value, now, throttleWindow))
        {
            log.LogDebug("Notify throttled for space {SpaceId} — within {ThrottleSeconds}s window.", spaceId.Value, _opts.ThrottleSeconds);
            return;
        }

        // Opportunistic prune: bound memory (mirrors NodeWakeCoordinator's dedup-map prune).
        var pruneThreshold = now - TimeSpan.FromSeconds(_opts.ThrottleSeconds * 2);
        foreach (var kvp in _lastNotifyAt)
            if (kvp.Value < pruneThreshold)
                _lastNotifyAt.TryRemove(kvp.Key, out _);

        IReadOnlyList<Node> nodes;
        IReadOnlyList<McpServer> servers;
        try
        {
            var nodesTask = locator.ListNodesAsync(spaceId.Value);
            var serversTask = locator.ListMcpServersAsync(spaceId.Value);
            await Task.WhenAll(nodesTask, serversTask).WaitAsync(ct);
            nodes = nodesTask.Result;
            servers = serversTask.Result;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to enumerate nodes/servers for access-request notify, space {SpaceId}.", spaceId.Value);
            // We won the claim but never got to attempt a push — don't burn the window (same
            // rationale as the zero-device case below).
            RollbackClaim(spaceId.Value, now);
            return;
        }

        var pushable = nodes.Where(n => !string.IsNullOrEmpty(n.PushToken) && !string.IsNullOrEmpty(n.PushPlatform)).ToList();
        if (pushable.Count == 0)
        {
            // no-op: owner has no push-enabled device — roll back the claim (post-review
            // correction) so a later request that DOES have a pushable device isn't wrongly
            // throttled by this one's optimistic stamp.
            RollbackClaim(spaceId.Value, now);
            return;
        }

        var serverName = servers.FirstOrDefault(s => s.Id == request.McpServerId)?.DisplayName
            ?? request.McpServerId.Value[..Math.Min(8, request.McpServerId.Value.Length)];

        var nodeNames = nodes.ToDictionary(n => n.Id.Value, n => n.DisplayName);
        Dictionary<string, string> agentNames;
        try
        {
            agentNames = await locator.ResolveAgentNamesAsync(
                new[] { request.ConsumerId.Value }, nodeNames, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to resolve agent name for access-request notify, space {SpaceId}.", spaceId.Value);
            agentNames = new Dictionary<string, string>(StringComparer.Ordinal);
        }
        var agentName = agentNames.GetValueOrDefault(
            request.ConsumerId.Value,
            request.ConsumerId.Value[..Math.Min(8, request.ConsumerId.Value.Length)]);

        var content = AlertContentFormatter.BuildNewRequestContent(agentName, serverName, request.Id.Value);

        var sendTasks = pushable.Select(node => SendToNodeAsync(node, content, ct)).ToList();
        await Task.WhenAll(sendTasks);
    }

    /// <summary>
    /// Atomically claims the per-space throttle window via a compare-and-set on the concurrent
    /// dictionary, so a burst of concurrent callers for the SAME space race on one atomic
    /// operation instead of a check-then-act gap. Returns true iff THIS call won the claim (i.e.
    /// is the one that should proceed to enumerate + push); false means another call's window is
    /// still live. <see cref="ConcurrentDictionary{TKey,TValue}.AddOrUpdate(TKey,Func{TKey,TValue},Func{TKey,TValue,TValue})"/>
    /// may re-invoke the factories under contention, but only the LAST invocation before a
    /// successful commit determines <paramref name="now"/>'s fate — so <c>wonClaim</c>, read only
    /// after the call returns, always reflects the value actually stored.
    /// </summary>
    private bool TryClaimWindow(string spaceKey, DateTimeOffset now, TimeSpan window)
    {
        var wonClaim = false;
        _lastNotifyAt.AddOrUpdate(
            spaceKey,
            addValueFactory: _ =>
            {
                // No prior stamp for this space — this call's stamp is the one committed.
                wonClaim = true;
                return now;
            },
            updateValueFactory: (_, existing) =>
            {
                if (now - existing < window)
                {
                    // Someone else's window is still live — this call is throttled; leave their
                    // stamp untouched.
                    wonClaim = false;
                    return existing;
                }
                // Outside the window — this call replaces the stamp and wins the claim.
                wonClaim = true;
                return now;
            });
        return wonClaim;
    }

    /// <summary>
    /// Un-claims a window this call itself claimed. Uses the collection's compare-and-remove
    /// (only removes the entry if it still equals exactly <paramref name="spaceKey"/> →
    /// <paramref name="claimedAt"/>) so a rollback can never clobber a stamp some other,
    /// genuinely later claim has since written.
    /// </summary>
    private void RollbackClaim(string spaceKey, DateTimeOffset claimedAt) =>
        ((ICollection<KeyValuePair<string, DateTimeOffset>>)_lastNotifyAt)
            .Remove(new KeyValuePair<string, DateTimeOffset>(spaceKey, claimedAt));

    private async Task SendToNodeAsync(Node node, AlertContent content, CancellationToken ct)
    {
        AlertSendResult result;
        try
        {
            result = await alertSender.SendAlertAsync(node.PushToken!, node.PushPlatform!, content, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Alert send threw for node {NodeId} — treated as failure.", node.Id.Value);
            return;
        }

        if (result == AlertSendResult.TokenInvalid)
        {
            try
            {
                await locator.GetNodeGrain(node.Id.Value).ClearPushTokenIfMatchesAsync(node.PushToken!);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Failed to clear stale push token for node {NodeId}.", node.Id.Value);
            }
        }
    }
}
