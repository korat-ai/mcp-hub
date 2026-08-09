using NATS.Client.Core;
using NATS.NKeys;

namespace Korat.Cloud;

/// <summary>
/// 009-nats-relay-backplane: maps a NATS_URL into <see cref="NatsOpts"/>.
///
/// Accepts one or more comma-separated server URLs:
///   nats://host:4222            — plaintext (correct inside Fly's private 6PN network)
///   tls://host:4222             — TLS required
/// On Fly we reach the NATS app over the
/// private network, so plaintext is the norm; tls:// is supported for hosted/public NATS.
///
/// 031-relay-confidentiality (N-1a): NKey authentication is additive.  When
/// <paramref name="nkeySeed"/> is non-null the NKey (Ed25519) credentials are attached to
/// the connection opts.  Absent ⇒ anonymous connect (backward-compatible with the pre-authz
/// broker so local/dev/CI setups without the Fly secret continue to work).
///
/// NATS NKey auth protocol: the server sends a nonce in INFO; the client responds with
/// CONNECT containing <c>nkey</c> (the public key) + <c>sig</c> (nonce signed with the
/// private key derived from the seed).  NATS.Net requires BOTH <c>NatsAuthOpts.NKey</c>
/// (public key) and <c>NatsAuthOpts.Seed</c> (seed) to be set — Seed alone is insufficient
/// because the server uses the public key to look up which permission set to apply before
/// verifying the signature.
/// </summary>
public static class NatsUrl
{
    public static NatsOpts ToOpts(string url, string name, string? nkeySeed = null)
    {
        var trimmed = url.Trim();
        var requiresTls = trimmed.StartsWith("tls://", StringComparison.OrdinalIgnoreCase);

        // 031 N-1a: attach NKey credentials when the seed is available.
        // Derive the public key from the seed so the caller only needs the seed.
        // When absent the broker is reached anonymously — correct for local/dev/CI without
        // the Fly secret (backward-compatible with the pre-authz no-auth broker).
        NatsAuthOpts authOpts;
        if (!string.IsNullOrWhiteSpace(nkeySeed))
        {
            var seed = nkeySeed.Trim();
            var kp = KeyPair.FromSeed(seed);
            authOpts = new NatsAuthOpts
            {
                NKey = kp.GetPublicKey(), // required: server maps pubkey → permission set
                Seed = seed,             // required: NATS.Net signs the server nonce
            };
        }
        else
        {
            authOpts = NatsAuthOpts.Default;
        }

        return NatsOpts.Default with
        {
            Url = trimmed,
            Name = name,
            AuthOpts = authOpts,
            TlsOpts = requiresTls
                ? new NatsTlsOpts { Mode = TlsMode.Require }
                : new NatsTlsOpts { Mode = TlsMode.Disable },
            // Survive transient NATS / network blips without crashing the silo.
            RetryOnInitialConnect = true,
            MaxReconnectRetry = -1,
        };
    }
}
