namespace Korat.Cloud.Web.Auth.Options;

/// <summary>
/// Options for CLI-facing endpoints (SP4 device flow).
/// </summary>
public sealed class CliOptions
{
    public const string SectionName = "Korat:Cli";

    /// <summary>
    /// The trusted public base URL of this Korat Cloud instance, e.g. "https://my.korat.ai".
    /// Used to build <c>verification_uri</c> and <c>verification_uri_complete</c> in the
    /// device-code response instead of echoing the client-supplied Host header (host-header
    /// injection defence).
    ///
    /// When not set the server falls back to <c>req.Scheme://req.Host</c> — acceptable in
    /// development (behind Kestrel on localhost) where AllowedHosts="*". In production,
    /// set this to the canonical public URL so the CLI always prints a safe, verifiable link.
    /// </summary>
    public string? PublicOrigin { get; set; }
}
