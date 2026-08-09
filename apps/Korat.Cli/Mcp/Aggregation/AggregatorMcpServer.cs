using System.Text.Json.Nodes;
using Korat.Cli.Commands;
using Korat.Cli.Auth;
using Korat.Mcp;

namespace Korat.Cli.Mcp.Aggregation;

/// <summary>
/// The stdio MCP server facing the local MCP client (Claude). Reads newline-delimited
/// JSON-RPC 2.0 from <see cref="RunAsync"/>'s reader, dispatches the MCP lifecycle and
/// tools methods, routes real tool calls to backend sessions, turns request-access tools
/// into access requests, and writes newline-delimited (flushed) responses. The
/// <see cref="SpaceWatcher"/> (T10) calls <see cref="EmitToolsListChangedAsync"/> when the
/// aggregate catalog changes. All writes go through one lock so replies and notifications
/// never interleave.
/// </summary>
internal sealed class AggregatorMcpServer
{
    // MCP protocol version used when the client doesn't request one (matches BackendSession).
    private const string DefaultProtocolVersion = "2025-06-18";

    private readonly AggregateCatalog _catalog;
    private readonly IBackendSessions _sessions;
    private readonly TextWriter _output;
    private readonly string _version;
    private readonly Func<CancellationToken, Task> _runUpgrade;

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _requestedLock = new();
    private readonly HashSet<string> _alreadyRequested = new();

    public AggregatorMcpServer(AggregateCatalog catalog, IBackendSessions sessions, TextWriter output, string version,
        Func<CancellationToken, Task>? runUpgrade = null)
    {
        _catalog = catalog;
        _sessions = sessions;
        _output = output;
        _version = version;
        _runUpgrade = runUpgrade ?? (async ct => await UpgradeCommand.RunAsync(yes: true));
    }

    public async Task RunAsync(TextReader input, CancellationToken ct)
    {
        try
        {
            string? line;
            while ((line = await input.ReadLineAsync(ct)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                JsonRpcMessage m;
                try { m = JsonRpcMessage.Parse(line); }
                catch { continue; } // malformed line with no recoverable id → skip

                try
                {
                    await DispatchAsync(m, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    // An unexpected exception from a handler must not crash the loop.
                    // If the message had an id, send a JSON-RPC internal-error so the
                    // client gets a response rather than hanging.
                    if (m.IsRequest && m.Id is not null)
                        await WriteAsync(JsonRpcMessage.Error(m.Id, -32603, ex.Message), ct);
                    else
                        await Console.Error.WriteLineAsync($"[korat] unhandled dispatch error: {ex.Message}");
                    // continue to next message
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // graceful shutdown
        }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested)
        {
            // stdin disposed to wake the blocking read on cancel — clean exit
        }
        catch (IOException) when (ct.IsCancellationRequested)
        {
            // same cancel-wake path
        }
    }

    private async Task DispatchAsync(JsonRpcMessage m, CancellationToken ct)
    {
        switch (m.Method)
        {
            case "initialize":
                await HandleInitializeAsync(m, ct);
                return;

            case "notifications/initialized":
                return; // no reply

            case "tools/list":
                await WriteAsync(JsonRpcMessage.Result(m.Id, _catalog.ToolsListJson()), ct);
                return;

            case "tools/call":
                await HandleToolCallAsync(m, ct);
                return;

            default:
                if (m.IsNotification) return; // ignore unknown notifications
                if (m.IsRequest)
                    await WriteAsync(JsonRpcMessage.Error(m.Id, -32601, $"method not found: {m.Method}"), ct);
                return;
        }
    }

    private async Task HandleInitializeAsync(JsonRpcMessage m, CancellationToken ct)
    {
        // Echo the client's requested protocolVersion (MCP-friendly); fall back to the default.
        var requested = m.Params?["protocolVersion"]?.GetValue<string>() ?? DefaultProtocolVersion;
        var init = new JsonObject
        {
            ["protocolVersion"] = requested,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = true },
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "korat-space",
                ["version"] = _version,
            },
        };
        await WriteAsync(JsonRpcMessage.Result(m.Id, init.ToJsonString()), ct);
    }

    private async Task HandleToolCallAsync(JsonRpcMessage m, CancellationToken ct)
    {
        var name = m.Params?["name"]?.GetValue<string>();
        if (name is null)
        {
            await WriteAsync(JsonRpcMessage.Error(m.Id, -32602, "missing tool name"), ct);
            return;
        }

        if (!_catalog.TryResolve(name, out var route) || route is null)
        {
            await WriteAsync(JsonRpcMessage.Error(m.Id, -32601, $"unknown tool: {name}"), ct);
            return;
        }

        switch (route.Kind)
        {
            case RouteKind.Tool:
                await HandleRealToolAsync(m, name, ct);
                return;
            case RouteKind.RequestAccess:
                await HandleRequestAccessAsync(m, route, ct);
                return;
            case RouteKind.Upgrade:
                await HandleUpgradeToolAsync(m, ct);
                return;
            case RouteKind.ControlListTools:
                await HandleControlListToolsAsync(m, ct);
                return;
            case RouteKind.ControlCallTool:
                await HandleControlCallToolAsync(m, ct);
                return;
            case RouteKind.ControlRequestAccess:
                await HandleControlRequestAccessAsync(m, ct);
                return;
            default:
                await WriteAsync(JsonRpcMessage.Error(m.Id, -32603, "unsupported tool route"), ct);
                return;
        }
    }

    private async Task HandleControlListToolsAsync(JsonRpcMessage m, CancellationToken ct)
    {
        var args = m.Params?["arguments"] as JsonObject;
        var prefix = args?["prefix"] is JsonValue prefixValue
            && prefixValue.TryGetValue<string>(out var parsedPrefix)
            ? parsedPrefix
            : null;
        var includeControlTools = args?["include_control_tools"] is JsonValue includeValue
            && includeValue.TryGetValue<bool>(out var parsedInclude)
            && parsedInclude;

        var source = JsonNode.Parse(_catalog.ToolsListJson())?["tools"] as JsonArray ?? new JsonArray();
        var tools = new JsonArray();
        foreach (var node in source)
        {
            if (node is not JsonObject tool
                || tool["name"] is not JsonValue nameValue
                || !nameValue.TryGetValue<string>(out var toolName))
            {
                continue;
            }

            if (!includeControlTools && AggregateCatalog.IsControlToolName(toolName))
                continue;
            if (!string.IsNullOrEmpty(prefix) && !toolName.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            tools.Add((JsonNode)tool.DeepClone());
        }

        await WriteToolTextResultAsync(
            m.Id,
            new JsonObject { ["tools"] = tools }.ToJsonString(),
            ct);
    }

    private async Task HandleControlCallToolAsync(JsonRpcMessage m, CancellationToken ct)
    {
        var args = m.Params?["arguments"] as JsonObject;
        var targetName = args?["name"] is JsonValue nameValue
            && nameValue.TryGetValue<string>(out var parsedName)
            ? parsedName
            : null;
        if (string.IsNullOrWhiteSpace(targetName))
        {
            await WriteAsync(JsonRpcMessage.Error(m.Id, -32602, "missing target tool name"), ct);
            return;
        }

        if (AggregateCatalog.IsControlToolName(targetName))
        {
            await WriteAsync(JsonRpcMessage.Error(m.Id, -32602, "control tools cannot be called recursively"), ct);
            return;
        }

        if (!_catalog.TryResolve(targetName, out var targetRoute)
            || targetRoute is null
            || targetRoute.Kind != RouteKind.Tool)
        {
            await WriteAsync(JsonRpcMessage.Error(m.Id, -32601, $"unknown namespaced tool: {targetName}"), ct);
            return;
        }

        JsonObject targetArguments;
        if (args?["arguments"] is null)
        {
            targetArguments = new JsonObject();
        }
        else if (args["arguments"] is JsonObject argumentObject)
        {
            targetArguments = (JsonObject)argumentObject.DeepClone();
        }
        else
        {
            await WriteAsync(JsonRpcMessage.Error(m.Id, -32602, "target arguments must be an object"), ct);
            return;
        }

        var targetParams = new JsonObject
        {
            ["name"] = targetName,
            ["arguments"] = targetArguments,
        };
        var targetMessage = JsonRpcMessage.Parse(
            JsonRpcMessage.Request(m.Id?.DeepClone() ?? JsonValue.Create(0), "tools/call", targetParams));
        await HandleRealToolAsync(targetMessage, targetName, ct);
    }

    private async Task HandleControlRequestAccessAsync(JsonRpcMessage m, CancellationToken ct)
    {
        var args = m.Params?["arguments"] as JsonObject;
        var target = args?["target"] is JsonValue targetValue
            && targetValue.TryGetValue<string>(out var parsedTarget)
            ? parsedTarget
            : null;
        if (string.IsNullOrWhiteSpace(target))
        {
            await WriteAsync(JsonRpcMessage.Error(m.Id, -32602, "missing access target"), ct);
            return;
        }

        var requestToolName = target.StartsWith("request-access" + ToolNamespacer.Separator, StringComparison.Ordinal)
            ? target
            : ToolNamespacer.RequestAccessTool(target);
        if (!_catalog.TryResolve(requestToolName, out var targetRoute)
            || targetRoute is null
            || targetRoute.Kind != RouteKind.RequestAccess)
        {
            await WriteAsync(JsonRpcMessage.Error(m.Id, -32601, $"unknown access target: {target}"), ct);
            return;
        }

        await HandleRequestAccessAsync(m, targetRoute, ct);
    }

    private async Task WriteToolTextResultAsync(JsonNode? id, string text, CancellationToken ct)
    {
        var content = new JsonArray
        {
            (JsonNode)new JsonObject { ["type"] = "text", ["text"] = text },
        };
        await WriteAsync(
            JsonRpcMessage.Result(id, new JsonObject { ["content"] = content }.ToJsonString()),
            ct);
    }

    private async Task HandleRealToolAsync(JsonRpcMessage m, string name, CancellationToken ct)
    {
        var args = m.Params?["arguments"]?.ToJsonString() ?? "{}";

        string backend;
        try
        {
            backend = await _sessions.CallAsync(name, args, m.Id!, ct);
        }
        catch (Exception ex)
        {
            await WriteAsync(JsonRpcMessage.Error(m.Id, -32603, ex.Message), ct);
            return;
        }

        JsonObject node;
        try { node = JsonNode.Parse(backend)!.AsObject(); }
        catch
        {
            await WriteAsync(JsonRpcMessage.Error(m.Id, -32603, "malformed backend response"), ct);
            return;
        }

        if (node["error"] is JsonObject err)
        {
            var code = err["code"]?.GetValue<int>() ?? -32603;
            var message = err["message"]?.GetValue<string>() ?? "backend error";
            await WriteAsync(JsonRpcMessage.Error(m.Id, code, message), ct);
            return;
        }

        if (node["result"] is JsonNode result)
        {
            await WriteAsync(JsonRpcMessage.Result(m.Id, result.ToJsonString()), ct);
            return;
        }

        await WriteAsync(JsonRpcMessage.Error(m.Id, -32603, "malformed backend response"), ct);
    }

    private async Task HandleRequestAccessAsync(JsonRpcMessage m, ToolRoute route, CancellationToken ct)
    {
        var serverId = route.ServerId!;
        var slug = route.Slug;

        AccessRequestResult result;
        try
        {
            result = await _sessions.RequestAccessAsync(serverId, ct);
        }
        catch (Exception ex)
        {
            await WriteAsync(JsonRpcMessage.Error(m.Id, -32603, ex.Message), ct);
            return;
        }

        string text;
        if (result.AlreadyGranted)
        {
            text = $"Access to '{slug}' is already granted — its tools will appear shortly.";
        }
        else
        {
            bool seenBefore;
            lock (_requestedLock)
            {
                seenBefore = _alreadyRequested.Contains(serverId);
                if (!seenBefore) _alreadyRequested.Add(serverId);
            }

            text = seenBefore
                ? $"Access to '{slug}' was already requested and is still pending owner approval."
                : $"Access requested for '{slug}'. The Space owner must approve it; the server's tools will then appear automatically.";
        }

        // Cast to JsonNode so the non-generic JsonArray.Add(JsonNode?) overload is bound
        // rather than the RequiresUnreferencedCode generic Add<T> (silences IL2026; trim-safe).
        var content = new JsonArray();
        content.Add((JsonNode)new JsonObject { ["type"] = "text", ["text"] = text });
        var resultPayload = new JsonObject { ["content"] = content };
        await WriteAsync(JsonRpcMessage.Result(m.Id, resultPayload.ToJsonString()), ct);
    }

    private async Task HandleUpgradeToolAsync(JsonRpcMessage m, CancellationToken ct)
    {
        string text;
        try
        {
            await _runUpgrade(ct);
            text = "Korat CLI upgrade attempted. Reconnect (re-run `korat connect`) for the new version to take effect.";
        }
        catch (Exception ex)
        {
            text = $"Upgrade failed: {ex.GetType().Name}: {ex.Message}. Run `korat upgrade` (or `brew upgrade korat`) manually.";
        }
        var content = new JsonArray { (JsonNode)new JsonObject { ["type"] = "text", ["text"] = text } };
        await WriteAsync(JsonRpcMessage.Result(m.Id, new JsonObject { ["content"] = content }.ToJsonString()), ct);
    }


    /// <summary>Emits an MCP <c>notifications/tools/list_changed</c> notification (called by SpaceWatcher).</summary>
    public Task EmitToolsListChangedAsync(CancellationToken ct = default)
        => WriteAsync(JsonRpcMessage.Notification("notifications/tools/list_changed"), ct);

    private async Task WriteAsync(string envelope, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await _output.WriteAsync(envelope.AsMemory(), ct);
            await _output.WriteAsync("\n".AsMemory(), ct);
            await _output.FlushAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
