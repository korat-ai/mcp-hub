using System.Text;
using Korat.Domain;

namespace Korat.Cloud.Gateways;

/// <summary>
/// 009-nats-relay-backplane: NATS subject scheme for the relay data plane.
///
/// NodeId / SessionId values are free-form strings supplied by nodes and may contain
/// characters that are illegal or dangerous in NATS subjects ('.', ' ', '*', '>'). We
/// URL-safe-base64-encode the raw value into a single subject token. This keeps subjects
/// valid AND prevents subject injection — a malicious NodeId cannot smuggle a wildcard
/// ('>'/'*') to subscribe to other nodes' inboxes.
/// </summary>
public static class NatsSubjects
{
    /// <summary>Per-node inbox: every machine subscribes here for its locally-connected nodes.</summary>
    public const string FramePrefix = "korat.relay.frame.";

    /// <summary>
    /// Per-session tap (designed, not yet subscribed). A future key-holding observer
    /// subscribes here to watch a session's stream — see specs/009 §"Future tap".
    /// DEFERRED: no subscriber is registered today; the subject constant is kept for
    /// forward compatibility so specs/009 TAP design can be implemented without a
    /// breaking subject-scheme change.
    /// </summary>
    public const string TapPrefix = "korat.relay.tap.";

    /// <summary>
    /// 022: per-connection inbox for agent streams (one per gRPC stream, keyed by ConnectionId).
    /// Uses a DISTINCT prefix from FramePrefix so connection subjects can never alias node subjects,
    /// regardless of the encoded value of the id (LOCKED #6, 022).
    /// </summary>
    public const string ConnPrefix = "korat.relay.conn.";

    /// <summary>
    /// 029: per-job inference event inbox. The serving silo subscribes here before dispatching
    /// InferenceRequest; the node's gRPC silo publishes each InferenceChunk/InferenceEnd here.
    /// Subject never aliases frame/conn/tap prefixes (distinct prefix "korat.relay.inf.").
    /// </summary>
    public const string InfPrefix = "korat.relay.inf.";

    public static string Frame(NodeId nodeId) => FramePrefix + Encode(nodeId.Value);

    /// <summary>022: per-connection inbox subject for an agent stream.</summary>
    public static string Conn(ConnectionId connectionId) => ConnPrefix + Encode(connectionId.Value);

    public static string Tap(SessionId sessionId) => TapPrefix + Encode(sessionId.Value);

    /// <summary>
    /// 029 / M2: per-job inference events subject, qualified by the owning node.
    /// Format: korat.relay.inf.{base64url(corrId)}.{base64url(nodeId)}
    /// Baking the owning node into the subject means a foreign node's chunks land on a
    /// subject nobody subscribes to — structural isolation even before the broker check.
    /// </summary>
    public static string Inf(string correlationId, NodeId owningNodeId) =>
        InfPrefix + Encode(correlationId) + "." + Encode(owningNodeId.Value);

    internal static string Encode(string raw)
    {
        var bytes = Encoding.UTF8.GetBytes(raw);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
