using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Korat.Cloud.Observability;

/// <summary>
/// 009-nats-relay-backplane: OpenTelemetry instruments for the relay. Send-only — the
/// OTLP exporter is wired in Program.cs only when OTEL_EXPORTER_OTLP_ENDPOINT is set;
/// these instruments always emit regardless (cheap no-op when nothing listens).
/// </summary>
public static class KoratTelemetry
{
    public const string SourceName = "Korat.Cloud.Relay";

    public static readonly ActivitySource ActivitySource = new(SourceName);

    public static readonly Meter Meter = new(SourceName);

    /// <summary>
    /// Count of MCP <c>tools/call</c> invocations observed on the relay, tagged with
    /// mcp.server.id / mcp.tool.name / korat.space.id. This is the "which tool of which
    /// MCP server was called" product signal.
    /// </summary>
    public static readonly Counter<long> ToolCalls =
        Meter.CreateCounter<long>("korat.mcp.tool_calls", unit: "calls", description: "MCP tools/call invocations observed on the relay");
}
