using System.Security.Cryptography;
using System.Text;

namespace Korat.Cloud.Security.Audit;

/// <summary>
/// 032: the hash-chain step. <c>RowHash = SHA256(UTF8(canonical) || PrevHash)</c>.
/// Standard library crypto only (System.Security.Cryptography) — no custom primitives.
/// </summary>
internal static class AuditHasher
{
    /// <summary>
    /// Chain genesis: SHA256("korat-audit-genesis-v1").
    /// MUST match the seed row inserted by the <c>AddAuditEvents</c> migration.
    /// </summary>
    internal static byte[] GenesisHash => SHA256.HashData(Encoding.UTF8.GetBytes("korat-audit-genesis-v1"));

    internal static byte[] ComputeRowHash(string canonical, byte[] prevHash)
    {
        ArgumentNullException.ThrowIfNull(prevHash);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(Encoding.UTF8.GetBytes(canonical));
        sha.AppendData(prevHash);
        return sha.GetHashAndReset();
    }
}
