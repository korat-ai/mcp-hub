using System.Text.Json;
using System.Text.Json.Nodes;
using Korat.Mcp;

namespace Korat.Cloud.Mcp.Http;

/// <summary>
/// Increment 1 (HTTP MCP direct-to-Space): minimal JSON-RPC 2.0 message wrapper for the
/// cloud-side Streamable-HTTP MCP client. Mirrors the shape (not the code) of the CLI's
/// HttpMcpMessage (apps/Korat.Cli/Mcp/Aggregation/JsonRpc.cs) — hand-rolled over
/// System.Text.Json.Nodes because no MCP SDK NuGet package exists in this repo
/// (Directory.Packages.props has no ModelContextProtocol/StreamJsonRpc reference).
/// </summary>
public sealed class HttpMcpMessage(JsonObject root)
{
    public JsonObject Root { get; } = root;

    public JsonNode? Id => Root.TryGetPropertyValue("id", out var id) ? id : null;
    public string? Method => Root.TryGetPropertyValue("method", out var m) ? m?.GetValue<string>() : null;
    public bool HasError => Root.ContainsKey("error");

    public static HttpMcpMessage Parse(string json) =>
        new((JsonObject)(JsonNode.Parse(json) ?? throw new FormatException("null json")));

    public static HttpMcpMessage Parse(byte[] utf8Bytes) => Parse(System.Text.Encoding.UTF8.GetString(utf8Bytes));

    public static HttpMcpMessage Request(object id, string method, object? @params = null)
    {
        var obj = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = JsonValue.Create(id),
            ["method"] = method
        };
        if (@params is not null)
            obj["params"] = JsonSerializer.SerializeToNode(@params);
        return new HttpMcpMessage(obj);
    }

    public static HttpMcpMessage Error(JsonNode? id, int code, string message) => new(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
    });

    public byte[] ToUtf8Bytes() => System.Text.Encoding.UTF8.GetBytes(Root.ToJsonString());

    public override string ToString() => Root.ToJsonString();
}
