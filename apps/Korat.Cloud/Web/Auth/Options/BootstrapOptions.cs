namespace Korat.Cloud.Web.Auth.Options;

/// <summary>
/// Configuration for the first-admin bootstrap mechanism.
/// When <see cref="AdminEmail"/> is set, a user signing in with that email
/// is provisioned as admin without requiring an invite (new user) or promoted
/// to admin if the account already exists (idempotent).
/// Leave unset (empty / null) to disable — all sign-ins require a valid invite.
/// </summary>
public sealed class BootstrapOptions
{
    public const string SectionName = "Bootstrap";

    /// <summary>
    /// The email address of the first admin.
    /// Fly secret name: <c>Bootstrap__AdminEmail</c>.
    /// Case-insensitive match; leading/trailing whitespace is ignored.
    /// Empty or unset → feature disabled, normal invite flow for everyone.
    /// </summary>
    public string? AdminEmail { get; set; }
}
