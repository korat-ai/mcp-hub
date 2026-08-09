using System.Text.Json.Nodes;

namespace Korat.Mcp;

/// <summary>
/// A parsed JSON-RPC 2.0 message (request / response / notification) over a JsonNode.
///
/// <para>Р32: this is the single definition. It used to exist twice — once in the Cloud's
/// Space-MCP namespace and once in the CLI aggregator — as a deliberate copy, on the reasoning
/// that the two would evolve independently. They did, but only in one direction: the SF-3
/// hardening of <see cref="Method"/> below (a peer sending <c>{"method":123}</c> made
/// <c>GetValue&lt;string&gt;</c> throw before the caller's own try/catch could frame it) was
/// applied to the Cloud copy and never reached the CLI. The CLI shipped the unpatched variant for
/// as long as both copies existed. That is the concrete cost this merge removes.</para>
///
/// <para>A THIRD type named JsonRpcMessage lives in the Cloud's HTTP-MCP client. It is not a copy
/// of this one — different surface, different job — and was renamed rather than merged, so that
/// one name means one thing.</para>
/// </summary>
public sealed class JsonRpcMessage
{
    private readonly JsonObject _root;
    private JsonRpcMessage(JsonObject root) => _root = root;

    public static JsonRpcMessage Parse(string line) =>
        new((JsonObject)(JsonNode.Parse(line) ?? throw new FormatException("null json")));

    public JsonNode? Id => _root["id"];
    public string? IdAsString => Id?.ToJsonString().Trim('"');

    // SF-3 (adversarial review): a malformed/malicious peer can send {"method":123} (or any
    // non-string "method" value) — the previous `_root["method"]?.GetValue<string>()` throws
    // InvalidOperationException in that case (GetValue<string> requires the underlying JSON
    // value to actually be a string), which would fault the caller before it ever reaches its
    // own try/catch framing. Defensive: only treat "method" as present when it really is a
    // JSON string value.
    public string? Method => _root["method"] is JsonValue v && v.TryGetValue<string>(out var m) ? m : null;

    public JsonObject? Params => _root["params"] as JsonObject;
    public bool HasResultOrError => _root.ContainsKey("result") || _root.ContainsKey("error");

    public bool IsRequest => Method is not null && Id is not null;
    public bool IsNotification => Method is not null && Id is null;
    public bool IsResponse => Method is null && HasResultOrError;

    public string Raw() => _root.ToJsonString();

    public static string Result(JsonNode? idNode, string resultJson)
    {
        var o = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = idNode?.DeepClone(), ["result"] = JsonNode.Parse(resultJson) };
        return o.ToJsonString();
    }

    public static string Error(JsonNode? idNode, int code, string message)
    {
        var o = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = idNode?.DeepClone(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        };
        return o.ToJsonString();
    }

    public static string Notification(string method, JsonObject? @params = null)
    {
        var o = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (@params is not null) o["params"] = @params;
        return o.ToJsonString();
    }

    /// <summary>Build a request envelope (used toward backend servers).</summary>
    public static string Request(JsonNode idNode, string method, JsonObject? @params = null)
    {
        var o = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = idNode, ["method"] = method };
        if (@params is not null) o["params"] = @params;
        return o.ToJsonString();
    }
}
