namespace Korat.Cloud.Push;

/// <summary>
/// Configuration for the FCM (Android) alert sender (031, mobile-push increment 2).
///
/// Config keys (Fly secrets):
///   Korat:Fcm:ProjectId          — Firebase project id.
///   Korat:Fcm:ServiceAccountJson — full service-account JSON (Fly secret, never committed).
///
/// Per-platform no-op: when either is absent, IAlertPushSender routes "fcm" to
/// NullAlertPushSender — APNs is unaffected (§4a, §6).
///
/// NOTE (naming): FirebaseAdmin.Messaging also declares its OWN `FcmOptions` type (a per-message
/// options bag, e.g. analytics_label — see Message.FcmOptions). This class is NEVER referenced
/// from a file that also `using`s FirebaseAdmin.Messaging with a bare `FcmOptions` — no ambiguity
/// in practice (C# resolves a same-namespace type over a `using`-imported one anyway), but is
/// called out here so a future maintainer isn't surprised by the shadow.
/// </summary>
public sealed class FcmOptions
{
    public const string SectionName = "Korat:Fcm";

    public string? ProjectId { get; set; }
    public string? ServiceAccountJson { get; set; }
}
