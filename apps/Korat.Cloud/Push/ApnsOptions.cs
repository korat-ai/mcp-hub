namespace Korat.Cloud.Push;

/// <summary>
/// Configuration for the Apple Push Notification service (APNs) sender used by push-to-wake (030).
/// All fields except WakeWaitSeconds and WakeDedupSeconds come from secrets — never commit values.
///
/// Config keys (Fly secrets / appsettings override):
///   Korat:Apns:KeyId            — APNs Auth Key ID (e.g. "ABC123DEFG")
///   Korat:Apns:TeamId           — Apple Developer Team ID (e.g. "ABCDE12345")
///   Korat:Apns:BundleId         — App bundle identifier (e.g. "dev.korat.node")
///   Korat:Apns:PrivateKeyPem    — full .p8 file contents (PEM with BEGIN/END lines) — Fly secret, NEVER committed
///   Korat:Apns:WakeWaitSeconds  — how long HandleRequestSession waits for the node to come online (default 12)
///   Korat:Apns:WakeDedupSeconds — minimum interval between pushes to the same node (default 10)
/// </summary>
public sealed class ApnsOptions
{
    public const string SectionName = "Korat:Apns";

    /// <summary>APNs Auth Key ID. When absent, <see cref="NullPushWakeSender"/> is used.</summary>
    public string? KeyId { get; set; }

    /// <summary>Apple Developer Team ID.</summary>
    public string? TeamId { get; set; }

    /// <summary>App bundle ID (apns-topic header).</summary>
    public string? BundleId { get; set; }

    /// <summary>
    /// PEM-encoded PKCS#8 private key (.p8 contents). Never log or commit.
    /// </summary>
    public string? PrivateKeyPem { get; set; }

    /// <summary>
    /// Seconds the wake coordinator waits for the node to come online after sending the push.
    /// Must be &lt; 15 s (20 s agent handshake timeout − 5 s safety margin).
    /// Default: 12.
    /// </summary>
    public int WakeWaitSeconds { get; set; } = 12;

    /// <summary>
    /// Minimum interval in seconds between silent pushes to the same node (dedup window).
    /// Default: 10.
    /// </summary>
    public int WakeDedupSeconds { get; set; } = 10;
}
