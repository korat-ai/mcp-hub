using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Korat.Protocol;
using Xunit;

namespace Korat.Protocol.Tests;

public sealed class TestVectorGenerationTests
{
    // Repo-root-relative path to the publishable fixtures.
    private static string VectorsDir =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "protocol", "test-vectors");

    // ── 031: E2E cipher vectors ───────────────────────────────────────────────────────────────────

    [Fact]
    public void E2eCipher_vectors_roundtrip_and_are_written()
    {
        // Fixed key — deterministic so SDKs can assert identical bytes.
        var key = new byte[32];
        for (var i = 0; i < 32; i++) key[i] = (byte)i;

        const string sessionId = "test-session-031";
        using var cipherA = new E2eSessionCipher(key, sessionId);
        using var cipherB = new E2eSessionCipher(key, sessionId);

        // Case 1: round-trip c2s seq=0 no meta
        var pt1 = Encoding.UTF8.GetBytes("hello korat e2e");
        var wire1 = cipherA.Seal(pt1, E2eSessionCipher.DirClientToServer);
        var opened1 = cipherB.Open(wire1, E2eSessionCipher.DirClientToServer, 0);
        Assert.Equal(pt1, opened1);

        // Case 2: round-trip s2c seq=0 no meta
        var pt2 = Encoding.UTF8.GetBytes("response from publisher");
        var wire2 = cipherA.Seal(pt2, E2eSessionCipher.DirServerToClient);
        var opened2 = cipherB.Open(wire2, E2eSessionCipher.DirServerToClient, 0);
        Assert.Equal(pt2, opened2);

        // Case 3: tampered tag must fail
        var wire3 = (byte[])wire1.Clone();
        wire3[0] ^= 0xFF;
        using var cipherC = new E2eSessionCipher(key, sessionId);
        Assert.ThrowsAny<CryptographicException>(() => cipherC.Open(wire3, E2eSessionCipher.DirClientToServer, 0));

        // Case 4: tampered ciphertext must fail
        var wire4 = (byte[])wire1.Clone();
        wire4[^1] ^= 0xFF;
        using var cipherD = new E2eSessionCipher(key, sessionId);
        Assert.ThrowsAny<CryptographicException>(() => cipherD.Open(wire4, E2eSessionCipher.DirClientToServer, 0));

        // Case 5: wrong direction must fail (sealed c2s, open as s2c)
        using var cipherE = new E2eSessionCipher(key, sessionId);
        Assert.ThrowsAny<CryptographicException>(() => cipherE.Open(wire1, E2eSessionCipher.DirServerToClient, 0));

        // Write vectors
        var doc = new JsonObject
        {
            ["scheme"] = "AES-256-GCM v1; nonce=dir(1)||0x000000(3)||seq(8BE); AAD='korat-frame-v1'||sessionId||0x00||dir||seq(8BE)||SHA256(meta); wire=tag(16)||ct",
            ["cases"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"]           = "roundtrip_c2s_seq0_no_meta",
                    ["key_hex"]        = Convert.ToHexString(key).ToLowerInvariant(),
                    ["session_id"]     = sessionId,
                    ["dir"]            = "0x00",
                    ["seq"]            = 0,
                    ["plaintext_utf8"] = "hello korat e2e",
                    ["wire_hex"]       = Convert.ToHexString(wire1).ToLowerInvariant(),
                },
                new JsonObject
                {
                    ["name"]           = "roundtrip_s2c_seq0_no_meta",
                    ["key_hex"]        = Convert.ToHexString(key).ToLowerInvariant(),
                    ["session_id"]     = sessionId,
                    ["dir"]            = "0x01",
                    ["seq"]            = 0,
                    ["plaintext_utf8"] = "response from publisher",
                    ["wire_hex"]       = Convert.ToHexString(wire2).ToLowerInvariant(),
                },
                new JsonObject
                {
                    ["name"]    = "tampered_tag_must_fail",
                    ["key_hex"] = Convert.ToHexString(key).ToLowerInvariant(),
                    ["session_id"] = sessionId,
                    ["dir"]     = "0x00",
                    ["seq"]     = 0,
                    ["wire_hex"] = Convert.ToHexString(wire3).ToLowerInvariant(),
                    ["expect"]  = "decrypt_error",
                },
                new JsonObject
                {
                    ["name"]    = "tampered_ciphertext_must_fail",
                    ["key_hex"] = Convert.ToHexString(key).ToLowerInvariant(),
                    ["session_id"] = sessionId,
                    ["dir"]     = "0x00",
                    ["seq"]     = 0,
                    ["wire_hex"] = Convert.ToHexString(wire4).ToLowerInvariant(),
                    ["expect"]  = "decrypt_error",
                },
                new JsonObject
                {
                    ["name"]          = "wrong_direction_must_fail",
                    ["key_hex"]       = Convert.ToHexString(key).ToLowerInvariant(),
                    ["session_id"]    = sessionId,
                    ["sealed_dir"]    = "0x00",
                    ["open_with_dir"] = "0x01",
                    ["seq"]           = 0,
                    ["wire_hex"]      = Convert.ToHexString(wire1).ToLowerInvariant(),
                    ["expect"]        = "decrypt_error",
                    ["note"]          = "direction byte is part of nonce; wrong dir → different nonce → AEAD fail",
                },
            },
        };

        Directory.CreateDirectory(VectorsDir);
        File.WriteAllText(
            Path.Combine(VectorsDir, "e2e-cipher.json"),
            doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    [Fact]
    public void NodeAuth_vectors_match_and_are_written()
    {
        // Fixed owner tokens + node ids → expected base64 HMAC-SHA256(ownerToken, nodeId).
        var samples = new (string Owner, string NodeId)[]
        {
            ("korat_owner_test_token_AAAA", "11111111111111111111111111111111"),
            ("korat_owner_test_token_BBBB", "22222222222222222222222222222222"),
        };

        var cases = new JsonArray();
        foreach (var (owner, nodeId) in samples)
        {
            var token = Korat.Domain.Auth.NodeAuthTokens.Compute(owner, nodeId);
            Assert.True(Korat.Domain.Auth.NodeAuthTokens.Verify(owner, nodeId, token));
            cases.Add(new JsonObject
            {
                ["owner_token"] = owner,
                ["node_id"] = nodeId,
                ["node_auth_token_base64"] = token,
            });
        }

        var doc = new JsonObject
        {
            ["scheme"] = "node_auth_token = base64(HMAC-SHA256(key=utf8(owner_token), msg=utf8(node_id)))",
            ["cases"] = cases,
        };

        Directory.CreateDirectory(VectorsDir);
        File.WriteAllText(
            Path.Combine(VectorsDir, "node-auth.json"),
            doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }
}
