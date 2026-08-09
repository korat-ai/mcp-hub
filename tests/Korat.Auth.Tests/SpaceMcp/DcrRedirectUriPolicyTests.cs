using Korat.Cloud.Web.Oauth;

namespace Korat.Auth.Tests.SpaceMcp;

/// <summary>
/// Space-MCP inc-2b, Task 3: the redirect-URI policy is the primary anti-open-redirect defense
/// for open DCR. A DCR client can register ANY string as a redirect target; if the policy is
/// loose, an attacker registers a client whose redirect_uri points at an attacker origin and
/// harvests authorization codes. These cases pin every allow/reject branch.
/// </summary>
public sealed class DcrRedirectUriPolicyTests
{
    [Theory]
    // ── Allowed: RFC 8252 loopback (Codex/Cursor) ──────────────────────────
    [InlineData("http://127.0.0.1:45123/callback")]
    [InlineData("http://127.0.0.1:0/cb")]            // ephemeral-port placeholder
    [InlineData("http://[::1]:8080/cb")]
    [InlineData("http://127.0.0.1/cb")]               // default port
    // ── Allowed: http://localhost loopback — the form the real MCP OAuth
    // clients (Claude Code, Cursor) actually register; safe per RFC 6761 §6.3
    // (special-use name, always loopback, never sent to DNS). ───────────────
    [InlineData("http://localhost:3000/cb")]          // MCP TS SDK loopback callback
    [InlineData("http://localhost/cb")]               // default port
    [InlineData("http://LOCALHOST:5000/cb")]          // Uri.Host lowercases → "localhost"
    // ── Allowed: exact-match https (Claude) ────────────────────────────────
    [InlineData("https://claude.ai/api/mcp/auth_callback")]
    [InlineData("https://claude.ai/api/mcp/auth_callback?x=1")]
    // ── Allowed (N3, documented, don't "fix"): .NET normalizes hex/decimal
    // IPv4 literals in the authority to the dotted-quad form; these ARE
    // genuinely loopback (Uri.Host == "127.0.0.1") and therefore safe to
    // accept under the same rule as "http://127.0.0.1/cb" above. Pinned
    // explicitly so a future policy-tightening refactor doesn't mistake this
    // for a hole. ─────────────────────────────────────────────────────────
    [InlineData("http://0x7f000001/cb")]              // hex IPv4 literal -> Host "127.0.0.1"
    [InlineData("http://2130706433/cb")]               // decimal IPv4 literal -> Host "127.0.0.1"
    public void Accepts_LoopbackAndHttps(string uri) =>
        Assert.Null(DcrRedirectUriPolicy.Validate(uri));

    [Theory]
    // ── Rejected: http non-loopback (the classic open-redirect vector) ─────
    [InlineData("http://evil.com/cb")]
    [InlineData("http://169.254.169.254/cb")]         // link-local / cloud metadata
    [InlineData("http://127.0.0.1.evil.com/cb")]      // suffix-smuggling the loopback literal
    [InlineData("http://localhost.evil.com/cb")]      // suffix-smuggling the "localhost" name — exact match, not suffix
    [InlineData("http://notlocalhost/cb")]            // "localhost" match is EXACT, not substring
    [InlineData("http://localhost.attacker.io:3000/cb")] // another localhost-suffix bait
    // ── Rejected (pinned per fable adversarial probe): "localhost" smuggling
    // vectors the accept path must NOT let through — a Unicode homoglyph host,
    // the name variant of userinfo bait (Host is "evil.com" + userinfo guard),
    // and the FQDN-rooted trailing-dot form (Host "localhost." ≠ "localhost"). ─
    [InlineData("http://localhоst/cb")]               // Cyrillic 'о' homoglyph → Host ≠ ASCII "localhost"
    [InlineData("http://localhost@evil.com/cb")]      // Host "evil.com"; userinfo "localhost" as bait
    [InlineData("http://localhost./cb")]              // trailing-dot FQDN root → Host "localhost." ≠ "localhost"
    // ── Rejected: wildcards, fragments, userinfo ──────────────────────────
    [InlineData("https://*.evil.com/cb")]             // unparseable-absolute → rejected
    [InlineData("https://claude.ai/cb#frag")]
    [InlineData("http://user:pass@127.0.0.1:9/cb")]
    [InlineData("http://127.0.0.1@evil.com/cb")]      // userinfo smuggling the loopback literal as bait
    // ── Rejected: other schemes / relative / junk ─────────────────────────
    [InlineData("myapp://callback")]
    [InlineData("ftp://127.0.0.1/cb")]
    [InlineData("/relative/callback")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a uri")]
    [InlineData("https://")]                           // empty host → fails absolute-URI parse
    [InlineData("http://\r\nSet-Cookie: x=1/cb")]      // CRLF-injection attempt
    // ── Rejected (N3, documented, don't "fix"): the IPv4-mapped IPv6 form is
    // STRICTER than the plain-loopback rule — .NET's Uri.Host for this
    // literal is "[::ffff:127.0.0.1]", which does not equal the literal
    // "[::1]" this policy allows. Genuinely safe to reject (no loopback
    // literal is being smuggled past the check); pinned explicitly so a
    // future refactor doesn't "fix" this into an accept. ───────────────────
    [InlineData("http://[::ffff:127.0.0.1]/cb")]
    public void Rejects_EverythingElse(string uri) =>
        Assert.NotNull(DcrRedirectUriPolicy.Validate(uri));
}
