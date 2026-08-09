using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Options;

namespace Korat.Cloud.Push;

/// <summary>
/// Thin seam around <see cref="FirebaseMessaging.SendAsync(Message, CancellationToken)"/> so
/// <see cref="FcmAlertSender"/> is unit-testable without a real Firebase app/credential or network
/// access (mirrors <see cref="IAccessRequestGrainLocator"/>'s role for AccessRequestNotifier).
/// </summary>
public interface IFcmMessagingClient
{
    Task<string> SendAsync(Message message, CancellationToken ct);
}

/// <summary>
/// Production adapter. Lazily creates the process-wide <see cref="FirebaseApp.DefaultInstance"/>
/// on first use, guarded by a lock — <see cref="FirebaseApp"/> is a static singleton PER PROCESS
/// (design §4a, §9): calling <see cref="FirebaseApp.Create(AppOptions)"/> a second time throws
/// ArgumentException, so this always checks DefaultInstance first.
/// </summary>
public sealed class FirebaseFcmMessagingClient : IFcmMessagingClient
{
    private static readonly object InitLock = new();
    private readonly FcmOptions _options;

    public FirebaseFcmMessagingClient(IOptions<FcmOptions> options) => _options = options.Value;

    public Task<string> SendAsync(Message message, CancellationToken ct)
    {
        FirebaseMessaging messaging;
        lock (InitLock)
        {
#pragma warning disable CS0618 // FromJson is Obsolete (Google flags it a security-risk pattern for
                               // untrusted/user-supplied JSON) — our ServiceAccountJson comes from a
                               // trusted Fly secret (FcmOptions), never from request input, so the
                               // risk the deprecation warns about doesn't apply here.
            var app = FirebaseApp.DefaultInstance ?? FirebaseApp.Create(new AppOptions
            {
                Credential = GoogleCredential.FromJson(_options.ServiceAccountJson),
                ProjectId = _options.ProjectId,
            });
#pragma warning restore CS0618
            messaging = FirebaseMessaging.GetMessaging(app);
        }
        return messaging.SendAsync(message, ct);
    }
}
