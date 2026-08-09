namespace Korat.Cli.Gateway;

/// <summary>
/// #104: tiered, human-readable console output for the E2E encryption handshake.
///
/// During a normal <c>korat connect --bridge</c> session the e2e handshake ran loud
/// security-engineer stderr ("[e2e] DOWNGRADE WARNING…", "[e2e] Session … is E2E-encrypted")
/// straight into the MCP client's (e.g. Claude Desktop) log stream, where it reads as alarming
/// and inscrutable. This helper tiers it:
///   • DEFAULT — one calm, plain-language line per outcome.
///   • <see cref="Verbose"/> (set by <c>connect --verbose/-v</c>) — the original protocol detail
///     prefixed <c>[e2e]</c>.
/// Security-critical outcomes (required-but-unavailable, handshake-failed-closing) are ALWAYS
/// shown regardless of verbosity — the user must know why a session closed.
/// </summary>
internal static class E2eConsole
{
    /// <summary>Set once from <c>connect --verbose</c>; process-wide (the CLI runs one command).</summary>
    internal static bool Verbose;

    private static void Line(string s) => Console.Error.WriteLine(s);

    /// <summary>Handshake succeeded — the session is end-to-end encrypted.</summary>
    internal static void Encrypted(string sessionId) =>
        Line(Verbose
            ? $"[e2e] Session {sessionId} is E2E-encrypted."
            : "Connection is end-to-end encrypted.");

    /// <summary>
    /// Peer can't (or won't) do E2E and policy permits plaintext (<c>--e2e=prefer</c>). Benign —
    /// the transport relay never sees payloads either way; E2E is the stronger, optional guarantee.
    /// </summary>
    internal static void FellBackToPlaintext(string sessionId, string reason) =>
        Line(Verbose
            ? $"[e2e] Downgrade ({reason}) for session {sessionId}; continuing in plaintext (--e2e=prefer)."
            : "Encryption unavailable — continuing in plaintext. Use --e2e=require to enforce it.");

    /// <summary>
    /// <c>--e2e=require</c> was set but E2E could not be established, so the session is closed
    /// (fail-closed). Always shown — it explains the closure.
    /// </summary>
    internal static void RequiredButUnavailable(string sessionId, string reason)
    {
        Line("Encryption required but unavailable — connection closed (--e2e=require).");
        if (Verbose) Line($"[e2e] {reason} for session {sessionId}; fail-closed as required by --e2e=require.");
    }

    /// <summary>
    /// The handshake failed cryptographically (broken confirm tag / crypto error). A failed key
    /// confirmation is positive evidence of active interference — NOT a peer that merely lacks
    /// encryption — so the session is closed even under <c>--e2e=prefer</c>. Always shown.
    /// </summary>
    internal static void HandshakeFailedClosing(string sessionId, string? detail = null)
    {
        Line("Encryption handshake failed — connection closed. A failed key confirmation indicates "
           + "active interference, not a peer that simply lacks encryption.");
        if (Verbose && detail is not null) Line($"[e2e] {detail} for session {sessionId}.");
    }

    /// <summary>
    /// Downgrade/injection attack detected: a non-E2E frame arrived on an established E2E
    /// session, or an encrypted frame arrived without a cipher. Always printed — this is a
    /// security abort. Protocol detail (enc value, session id) is verbose-only.
    /// </summary>
    internal static void DowngradeAttackDetected(string sessionId, uint enc)
    {
        Line("Encryption attack detected — connection closed immediately. "
           + "A non-encrypted frame was received on an established encrypted session.");
        if (Verbose) Line($"[e2e] DOWNGRADE/INJECTION ATTACK DETECTED: session {sessionId} received enc={enc} frame after E2E was established.");
    }

    /// <summary>
    /// Protocol error: an enc!=0 frame arrived when no E2E session was negotiated (or vice
    /// versa). Always printed — the session is closed. Detail behind verbose.
    /// </summary>
    internal static void EncCipherMismatch(string sessionId, uint enc, bool hasCipher)
    {
        Line("Encryption protocol error — connection closed. "
           + "The encryption state of an incoming frame did not match the negotiated session.");
        if (Verbose) Line($"[e2e] enc/cipher mismatch session={sessionId} enc={enc} cipher={hasCipher}.");
    }

    /// <summary>
    /// Verbose-only protocol diagnostic; prints nothing in the default tier.
    /// </summary>
    internal static void Detail(string message)
    {
        if (Verbose) Line($"[e2e] {message}");
    }

    /// <summary>
    /// <c>--e2e=require</c> was set but the aggregator session's E2E handshake failed.
    /// Always shown — explains the fail-closed closure. Detail behind verbose.
    /// </summary>
    internal static void RequireFailedForServer(string sessionId, string serverName)
    {
        Line($"Encryption required but unavailable for '{serverName}' — session closed (--e2e=require).");
        if (Verbose) Line($"[e2e] REQUIRE: E2E handshake failed for aggregator session {sessionId} ({serverName}). Fail-closed.");
    }
}
