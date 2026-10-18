using System.Text.Json.Nodes;
using Korat.Domain;
using Korat.Domain.Entities;
using Korat.Mcp;

namespace Korat.Cloud.Mcp.Space;

public sealed partial class SpaceMcpAggregatorGrain
{
    private readonly McpContentCatalog _content = new();
    private readonly HashSet<string> _dirtyCatalogs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _refreshingCatalogs = new(StringComparer.Ordinal);
    private string _announcedResources = "";
    private string _announcedTemplates = "";
    private string _announcedPrompts = "";

    private sealed record Subscription(string ServerId, string Uri);
    // Value identifies the upstream session that actually accepted the subscription.
    private readonly Dictionary<Subscription, string> _subscriptions = new();
    private sealed class PendingSubscription(string sessionId)
    {
        public string SessionId { get; } = sessionId;
        public bool Updated { get; set; }
    }
    private readonly Dictionary<Subscription, PendingSubscription> _pendingSubscriptions = new();
    private readonly HashSet<string> _restoringSubscriptions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _subscriptionGate = new(1, 1);
    private const int MaxSubscriptions = 256;

    private sealed record PendingNotification(long Cursor, string Json, Subscription? Subscription);
    // Coalesce invalidations rather than growing an unbounded event log. At most three list
    // changes plus one update per subscribed URI. Updates mean "read again", not event history.
    private readonly Dictionary<string, PendingNotification> _notifications = new(StringComparer.Ordinal);
    private long _notificationCursor;
    private long _deliveredNotificationCursor;
    private TaskCompletionSource _notificationSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string? StringValue(JsonNode? value) =>
        value is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;

    private static bool StringArguments(JsonNode? value) => value is null
        || value is JsonObject obj && obj.All(p => StringValue(p.Value) is not null);

    private async Task<bool> IsServerGrantedAsync(string serverId, CancellationToken ct) =>
        await GetGrantedServerAsync(serverId, ct) is not null;

    private async Task<McpServer?> GetGrantedServerAsync(string serverId, CancellationToken ct)
    {
        var id = new McpServerId(serverId);
        var server = await repository.GetMcpServerAsync(id, ct);
        if (_tornDown || server is null || server.SpaceId != _spaceId
            || server.Status != McpServerStatus.Published) return null;
        var grant = await repository.GetActiveGrantAsync(_spaceId, _consumerIdentity, id, ct);
        return !_tornDown && grant is not null
            && string.Equals(grant.ApprovedDefinitionDigest, McpServerDefinition.Digest(server), StringComparison.Ordinal)
                ? server : null;
    }

    private async Task PruneUnauthorizedContentAsync()
    {
        foreach (var serverId in _reservedSlugsByServerId.Keys.ToArray())
        {
            if (await IsServerGrantedAsync(serverId, CancellationToken.None)) continue;
            RemoveContent(serverId);
            if (_catalog.RemoveGranted(serverId)) BumpListChanged();
        }
        AnnounceContentChanges();
    }

    private void RemoveContent(string serverId)
    {
        _content.RemoveServer(serverId);
        foreach (var key in _subscriptions.Keys.Where(k => k.ServerId == serverId).ToArray())
        {
            _subscriptions.Remove(key);
            _notifications.Remove(McpContentCatalog.WrapResourceUri(key.ServerId, key.Uri));
        }
        AnnounceContentChanges();
    }

    private async Task<string> HandleContentAsync(JsonRpcMessage msg)
    {
        var parameters = msg.Params;
        McpContentRoute? route;
        JsonObject forwarded;
        string capability;
        if (msg.Method == "prompts/get")
        {
            var name = StringValue(parameters?["name"]);
            if (string.IsNullOrEmpty(name) || !StringArguments(parameters?["arguments"]))
                return JsonRpcMessage.Error(msg.Id, -32602, "Expected a prompt name and string arguments.");
            if (name == KoratGuide.PromptName)
                return JsonRpcMessage.Result(msg.Id, KoratGuide.GetPrompt(parameters?["arguments"] as JsonObject).ToJsonString());
            if (!_content.TryResolvePrompt(name, out route) || route is null)
                return JsonRpcMessage.Error(msg.Id, -32602, "Unknown prompt.");
            forwarded = (JsonObject)parameters!.DeepClone();
            forwarded["name"] = route.OriginalValue;
            capability = "prompts";
        }
        else if (msg.Method == "resources/read")
        {
            var uri = StringValue(parameters?["uri"]);
            if (string.IsNullOrEmpty(uri))
                return JsonRpcMessage.Error(msg.Id, -32602, "Expected a resource URI.");
            if (uri == KoratGuide.ResourceUri)
                return JsonRpcMessage.Result(msg.Id, KoratGuide.ReadResource().ToJsonString());
            if (!_content.TryResolveResource(uri, out route) || route is null)
                return JsonRpcMessage.Error(msg.Id, -32002, "Resource not found.");
            forwarded = (JsonObject)parameters!.DeepClone();
            forwarded["uri"] = route.OriginalValue;
            capability = "resources";
        }
        else
        {
            if (parameters?["ref"] is not JsonObject reference
                || parameters["argument"] is not JsonObject argument
                || StringValue(argument["name"]) is not { Length: > 0 }
                || StringValue(argument["value"]) is null
                || (parameters["context"] is not null && parameters["context"] is not JsonObject)
                || !StringArguments(parameters["context"]?["arguments"]))
                return JsonRpcMessage.Error(msg.Id, -32602, "Expected a completion reference and string argument.");
            var type = StringValue(reference["type"]);
            var value = StringValue(reference[type == "ref/prompt" ? "name" : "uri"]);
            if (value is null || type is not ("ref/prompt" or "ref/resource"))
                return JsonRpcMessage.Error(msg.Id, -32602, "Unknown completion reference.");
            if (type == "ref/prompt" && value == KoratGuide.PromptName
                || type == "ref/resource" && value == KoratGuide.ResourceUri)
                return EmptyCompletion(msg.Id);
            var resolved = type == "ref/prompt"
                ? _content.TryResolvePrompt(value, out route)
                : _content.TryResolveResourceTemplate(value, out route)
                    || _content.TryResolveResource(value, out route);
            if (!resolved || route is null)
                return JsonRpcMessage.Error(msg.Id, -32602, "Unknown completion reference.");
            forwarded = (JsonObject)parameters.DeepClone();
            forwarded["ref"]![type == "ref/prompt" ? "name" : "uri"] = route.OriginalValue;
            capability = "completions";
        }

        var backend = await OpenBackendForToolCallAsync(route.ServerId);
        if (backend is null) return JsonRpcMessage.Error(msg.Id, -32000, "Server unavailable.");
        if (!backend.Supports(capability))
            return capability == "completions" ? EmptyCompletion(msg.Id)
                : JsonRpcMessage.Error(msg.Id, -32601, "The source server does not support this operation.");
        return await ForwardContentRequestAsync(msg, backend, forwarded);
    }

    private static string EmptyCompletion(JsonNode? id) =>
        JsonRpcMessage.Result(id, "{\"completion\":{\"values\":[],\"hasMore\":false}}");

    private async Task<string> ForwardContentRequestAsync(JsonRpcMessage msg, SpaceBackendSession backend,
        JsonObject parameters)
    {
        try
        {
            var response = await backend.SendRequestAsync(msg.Method!, parameters, CancellationToken.None,
                msg.Method is "resources/subscribe" or "resources/unsubscribe"
                    ? SpaceBackendSession.HandshakeTimeout : SpaceBackendSession.ToolCallTimeout);
            // A grant can be revoked while the server is preparing its response. The data must
            // not leave this session after a revoke even if the backend answered successfully.
            if (!backend.IsAlive || !await IsServerGrantedAsync(backend.ServerId, CancellationToken.None))
                return JsonRpcMessage.Error(msg.Id, -32000, "Server unavailable.");
            var envelope = JsonNode.Parse(response.Raw())!;
            if (envelope["error"] is JsonObject error)
            {
                if (error["code"] is not JsonValue code || !code.TryGetValue<int>(out var number)
                    || StringValue(error["message"]) is not { } message)
                    return JsonRpcMessage.Error(msg.Id, -32603, "Malformed backend response.");
                return JsonRpcMessage.Error(msg.Id, number, message, error["data"]);
            }
            return envelope["result"] is JsonObject result
                ? JsonRpcMessage.Result(msg.Id, McpContentCatalog.RewriteResult(backend.ServerId, result).ToJsonString())
                : JsonRpcMessage.Error(msg.Id, -32603, "Malformed backend response.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Space-MCP: content request failed method={Method} serverId={ServerId}", msg.Method, backend.ServerId);
            if (!backend.IsAlive) EvictDeadBackendLocal(backend);
            return JsonRpcMessage.Error(msg.Id, -32000, "Server unavailable.");
        }
    }

    private async Task<string> HandleSubscriptionAsync(JsonRpcMessage msg)
    {
        var uri = StringValue(msg.Params?["uri"]);
        if (string.IsNullOrEmpty(uri)) return JsonRpcMessage.Error(msg.Id, -32602, "Expected a resource URI.");
        if (uri == KoratGuide.ResourceUri) return JsonRpcMessage.Result(msg.Id, "{}"); // Immutable.
        if (!_content.TryResolveResource(uri, out var route) || route is null)
            return JsonRpcMessage.Error(msg.Id, -32002, "Resource not found.");
        if (!await _subscriptionGate.WaitAsync(SpaceBackendSession.HandshakeTimeout))
            return JsonRpcMessage.Error(msg.Id, -32000, "A subscription change is still in progress.");
        try
        {
            var key = new Subscription(route.ServerId, route.OriginalValue);
            var subscribe = msg.Method == "resources/subscribe";
            if (!subscribe)
            {
                // Cancelling local intent must not require waking a sleeping publisher. A
                // racing restore checks this entry again before committing its result.
                var existed = _subscriptions.Remove(key);
                _notifications.Remove(McpContentCatalog.WrapResourceUri(key.ServerId, key.Uri));
                if (!existed || FindLiveBackend(route.ServerId) is null)
                    return JsonRpcMessage.Result(msg.Id, "{}");
            }
            var backend = await OpenBackendForToolCallAsync(route.ServerId);
            if (backend is null) return JsonRpcMessage.Error(msg.Id, -32000, "Server unavailable.");
            if (!backend.SupportsSubscriptions)
                return JsonRpcMessage.Error(msg.Id, -32601, "The source server does not support subscriptions.");
            if (subscribe && _subscriptions.TryGetValue(key, out var session) && session == backend.SessionId)
                return JsonRpcMessage.Result(msg.Id, "{}");
            if (subscribe && _subscriptions.Count >= MaxSubscriptions)
                return JsonRpcMessage.Error(msg.Id, -32000, "Resource subscription limit reached.");
            var forwarded = (JsonObject)msg.Params!.DeepClone();
            forwarded["uri"] = route.OriginalValue;
            var pending = new PendingSubscription(backend.SessionId);
            if (subscribe) _pendingSubscriptions[key] = pending;
            try
            {
                var response = await ForwardContentRequestAsync(msg, backend, forwarded);
                if (subscribe && JsonNode.Parse(response)?["error"] is null)
                {
                    _subscriptions[key] = backend.SessionId;
                    if (pending.Updated) PublishResourceUpdated(key);
                }
                return response;
            }
            finally { if (subscribe) _pendingSubscriptions.Remove(key); }
        }
        finally { _subscriptionGate.Release(); }
    }

    private void StartRestoreSubscriptions(SpaceBackendSession backend)
    {
        if (_tornDown || !backend.IsAlive
            || !_subscriptions.Any(p => p.Key.ServerId == backend.ServerId && p.Value != backend.SessionId)
            || !_restoringSubscriptions.Add(backend.SessionId)) return;
        // Stay on the grain scheduler, but do not make discovery/open await subscription RPCs.
        // A slow watch must not take unrelated tools, reads, or prompts offline.
        _ = RestoreSubscriptionsAsync(backend);
    }

    private bool IsCurrentBackend(SpaceBackendSession backend) => !_tornDown && backend.IsAlive
        && _backendsBySessionId.TryGetValue(backend.SessionId, out var current) && ReferenceEquals(current, backend);

    private async Task RestoreSubscriptionsAsync(SpaceBackendSession backend)
    {
        try
        {
            using var concurrency = new SemaphoreSlim(4, 4);
            var pending = _subscriptions.Where(p => p.Key.ServerId == backend.ServerId && p.Value != backend.SessionId).ToArray();
            await Task.WhenAll(pending.Select(async pair =>
            {
                await concurrency.WaitAsync();
                try { await RestoreSubscriptionAsync(backend, pair); }
                finally { concurrency.Release(); }
            }));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Space-MCP: subscription restore failed serverId={ServerId}", backend.ServerId);
        }
        finally { _restoringSubscriptions.Remove(backend.SessionId); }
    }

    private async Task RestoreSubscriptionAsync(SpaceBackendSession backend, KeyValuePair<Subscription, string> pair)
    {
        try
        {
            using var timeout = new CancellationTokenSource(SpaceBackendSession.HandshakeTimeout);
            var ct = timeout.Token;
            if (!IsCurrentBackend(backend)
                || !_subscriptions.TryGetValue(pair.Key, out var previousSession) || previousSession != pair.Value
                || !await IsServerGrantedAsync(backend.ServerId, ct)) return;
            if (!backend.SupportsSubscriptions)
            {
                _subscriptions.Remove(pair.Key);
                _notifications.Remove(McpContentCatalog.WrapResourceUri(pair.Key.ServerId, pair.Key.Uri));
                return;
            }
            var reply = await backend.SendRequestAsync("resources/subscribe", new JsonObject { ["uri"] = pair.Key.Uri },
                ct, SpaceBackendSession.HandshakeTimeout);
            if (!IsCurrentBackend(backend) || !await IsServerGrantedAsync(backend.ServerId, ct)) return;
            var envelope = JsonNode.Parse(reply.Raw());
            if (envelope?["error"] is JsonObject)
            {
                // A removed resource or a rejected subscription must not poison the whole
                // server's reconnect (and all its unrelated tools/resources/prompts).
                if (_subscriptions.TryGetValue(pair.Key, out var rejectedSession) && rejectedSession == pair.Value)
                {
                    _subscriptions.Remove(pair.Key);
                    _notifications.Remove(McpContentCatalog.WrapResourceUri(pair.Key.ServerId, pair.Key.Uri));
                    PublishNotification("notifications/resources/list_changed");
                }
                return;
            }
            if (envelope?["result"] is not JsonObject)
                throw new InvalidOperationException("Could not restore resource subscription.");
            if (_subscriptions.TryGetValue(pair.Key, out var session) && session == pair.Value)
            {
                _subscriptions[pair.Key] = backend.SessionId;
                // Changes may have happened while disconnected; tell this subscriber to read again.
                PublishResourceUpdated(pair.Key);
            }
            else if (!_subscriptions.ContainsKey(pair.Key) && !_pendingSubscriptions.ContainsKey(pair.Key))
            {
                // Unsubscribed while this request was in flight. Do not undo a newer explicit
                // subscribe which already succeeded (or is still pending) on this backend.
                // Serialize this undo with explicit changes: sending itself can await routing,
                // so a check alone would let an undo overtake and cancel a newer subscription.
                if (!await _subscriptionGate.WaitAsync(SpaceBackendSession.HandshakeTimeout, ct)) return;
                try
                {
                    if (IsCurrentBackend(backend) && !_subscriptions.ContainsKey(pair.Key) && !_pendingSubscriptions.ContainsKey(pair.Key))
                        await backend.SendRequestAsync("resources/unsubscribe", new JsonObject { ["uri"] = pair.Key.Uri },
                            ct, SpaceBackendSession.HandshakeTimeout);
                }
                finally { _subscriptionGate.Release(); }
            }
        }
        catch (Exception ex)
        {
            // Preserve intent after transient transport/timeout failures. The next reconcile
            // retries only missing watches; successfully restored peers are not replayed.
            logger.LogWarning(ex, "Space-MCP: resource subscription restore deferred serverId={ServerId}", backend.ServerId);
        }
    }

    private void OnBackendNotification(SpaceBackendSession backend, string method, JsonObject? parameters)
    {
        if (_tornDown || !backend.IsAlive) return;
        if (method is "notifications/tools/list_changed" or "notifications/resources/list_changed" or "notifications/prompts/list_changed")
        {
            _dirtyCatalogs.Add(backend.SessionId);
            return; // Do not await a backend reply from inside its inbound delivery.
        }
        if (method == "notifications/resources/updated" && StringValue(parameters?["uri"]) is { } uri)
        {
            var key = new Subscription(backend.ServerId, uri);
            if (_subscriptions.TryGetValue(key, out var session) && session == backend.SessionId)
                PublishResourceUpdated(key);
            else if (_pendingSubscriptions.TryGetValue(key, out var pending) && pending.SessionId == backend.SessionId)
                pending.Updated = true;
        }
    }

    private void StartDirtyCatalogRefreshes(CancellationToken ct)
    {
        if (_tornDown) return;
        foreach (var sessionId in _dirtyCatalogs.ToArray())
        {
            // A notification during a refresh keeps its dirty bit for the next pass.
            // Each backend has at most one refresh; a hung peer must not hold up others.
            if (_refreshingCatalogs.Contains(sessionId)) continue;
            _dirtyCatalogs.Remove(sessionId);
            if (!_backendsBySessionId.TryGetValue(sessionId, out var backend) || !backend.IsAlive) continue;
            _refreshingCatalogs.Add(sessionId);
            // Stay on the grain scheduler. This task observes its own failures and teardown
            // closes the backend, releasing any pending request; no Task.Run is needed.
            _ = RefreshCatalogAsync(backend, ct);
        }
    }

    private async Task RefreshCatalogAsync(SpaceBackendSession backend, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(PerBackendTimeout);
            var tools = await backend.ListToolsAsync(timeout.Token);
            await backend.ListContentAsync(timeout.Token);
            var server = await GetGrantedServerAsync(backend.ServerId, timeout.Token);
            // Permission or backend session may have changed while any await was pending.
            if (server is null || !IsCurrentBackend(backend)) return;
            _content.SetServer(backend.ServerId, backend.Slug, backend.Resources, backend.ResourceTemplates, backend.Prompts);
            _catalog.SetGranted(backend.ServerId, backend.Slug, server.DisplayName, tools);
            if (_catalog.ToolsListJson() != _announcedToolsListJson) BumpListChanged();
            else AnnounceContentChanges();
        }
        catch (Exception ex)
        {
            // Retry on a later pass, not in a tight loop on a failing backend.
            if (IsCurrentBackend(backend)) _dirtyCatalogs.Add(backend.SessionId);
            logger.LogWarning(ex, "Space-MCP: catalog refresh failed serverId={ServerId}", backend.ServerId);
        }
        finally { _refreshingCatalogs.Remove(backend.SessionId); }
    }

    private void RememberContentSnapshot()
    {
        _announcedResources = _content.ResourcesListJson();
        _announcedTemplates = _content.ResourceTemplatesListJson();
        _announcedPrompts = _content.PromptsListJson();
    }

    private void AnnounceContentChanges()
    {
        if (_announcedResources != _content.ResourcesListJson() || _announcedTemplates != _content.ResourceTemplatesListJson())
            PublishNotification("notifications/resources/list_changed");
        if (_announcedPrompts != _content.PromptsListJson()) PublishNotification("notifications/prompts/list_changed");
        RememberContentSnapshot();
    }

    private void PublishResourceUpdated(Subscription key) => PublishNotification("notifications/resources/updated",
        new JsonObject { ["uri"] = McpContentCatalog.WrapResourceUri(key.ServerId, key.Uri) }, key);

    private void PublishNotification(string method, JsonObject? parameters = null, Subscription? subscription = null)
    {
        var key = subscription is null ? method : McpContentCatalog.WrapResourceUri(subscription.ServerId, subscription.Uri);
        _notifications[key] = new(++_notificationCursor, JsonRpcMessage.Notification(method, parameters), subscription);
        var signal = _notificationSignal;
        _notificationSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.TrySetResult();
    }

    public async Task<SpaceMcpNotifications> NextNotificationsAsync(long knownCursor, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var after = Math.Max(knownCursor, _deliveredNotificationCursor);
        if (!_tornDown && _notificationCursor <= after)
        {
            try { await _notificationSignal.Task.WaitAsync(ListChangedHeartbeat, cancellationToken); }
            catch (TimeoutException) { }
        }
        if (_tornDown) return new(_notificationCursor, []);
        var pending = _notifications.Values.Where(n => n.Cursor > Math.Max(knownCursor, _deliveredNotificationCursor)).ToArray();
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in pending.Where(n => n.Subscription is not null).Select(n => n.Subscription!.ServerId).Distinct())
            if (await IsServerGrantedAsync(id, cancellationToken)) allowed.Add(id);
        cancellationToken.ThrowIfCancellationRequested();
        if (_tornDown) return new(_notificationCursor, []);
        // Snapshot cursor with this batch, not after awaits (which can admit newer events).
        var cursor = pending.Length > 0 ? pending.Max(n => n.Cursor) : _notificationCursor;
        // A session can have several GET streams. Assign each invalidation to exactly one,
        // after the authorization awaits: another reader may have claimed this batch meanwhile.
        // No event IDs/replay are advertised; delivery is best-effort, not an event history.
        var messages = pending.Where(n => n.Cursor > _deliveredNotificationCursor
                && (n.Subscription is null
                    || allowed.Contains(n.Subscription.ServerId) && _subscriptions.ContainsKey(n.Subscription)))
            .OrderBy(n => n.Cursor).Select(n => n.Json).ToArray();
        _deliveredNotificationCursor = Math.Max(_deliveredNotificationCursor, cursor);
        return new(cursor, messages);
    }
}
