using System.Diagnostics;

namespace Korat.Cloud.Observability;

/// <summary>
/// 009-nats-relay-backplane: a single observed MCP <c>tools/call</c> — the product
/// metadata "which tool of which MCP server was called".
/// </summary>
public readonly record struct ToolCallEvent(string SpaceId, string McpServerId, string ToolName, string Direction);

/// <summary>Where observed tool calls go. Default = OpenTelemetry; tests use a capturing sink.</summary>
public interface IMcpToolCallSink
{
    void Record(in ToolCallEvent toolCall);
}

/// <summary>Emits each observed tool call as an OTel counter increment + a short activity.</summary>
public sealed class OpenTelemetryToolCallSink : IMcpToolCallSink
{
    public void Record(in ToolCallEvent toolCall)
    {
        KoratTelemetry.ToolCalls.Add(
            1,
            new KeyValuePair<string, object?>("mcp.server.id", toolCall.McpServerId),
            new KeyValuePair<string, object?>("mcp.tool.name", toolCall.ToolName),
            new KeyValuePair<string, object?>("korat.space.id", toolCall.SpaceId));

        using var activity = KoratTelemetry.ActivitySource.StartActivity("mcp.tool.call", ActivityKind.Internal);
        if (activity is not null)
        {
            activity.SetTag("mcp.server.id", toolCall.McpServerId);
            activity.SetTag("mcp.tool.name", toolCall.ToolName);
            activity.SetTag("korat.space.id", toolCall.SpaceId);
            activity.SetTag("korat.relay.direction", toolCall.Direction);
        }
    }
}
