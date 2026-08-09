using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Korat.Cloud.Web.Auth.Services;

public sealed class PendingLinkService(IDataProtectionProvider provider, TimeProvider time) : IPendingLinkService
{
    private readonly IDataProtector _protector = provider.CreateProtector("Korat.Auth.PendingLink.v1");

    public string Issue(PendingLink link) =>
        Convert.ToBase64String(_protector.Protect(JsonSerializer.SerializeToUtf8Bytes(link)))
               .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public PendingLink? TryRead(string protectedValue)
    {
        // DoS guard: TryRead is reachable on an unauthenticated SPA round-trip. Mirror the
        // OAuthStateProtector length cap (4096 chars) — bounds attacker amplification on
        // unbounded base64-decode + AES unwrap.
        if (string.IsNullOrEmpty(protectedValue) || protectedValue.Length > 4096) return null;

        try
        {
            var padded = protectedValue.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            var bytes = Convert.FromBase64String(padded);
            var link = JsonSerializer.Deserialize<PendingLink>(_protector.Unprotect(bytes));
            if (link is null || link.ExpiresAt < time.GetUtcNow()) return null;
            return link;
        }
        catch { return null; }
    }
}
