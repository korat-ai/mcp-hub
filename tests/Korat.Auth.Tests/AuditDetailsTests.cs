using Korat.Cloud.Security.Audit;

namespace Korat.Auth.Tests;

/// <summary>
/// Deferred-fix (consistency): all DetailsJson is now built via <see cref="AuditDetails"/>
/// (one shared System.Text.Json options instance). The hash chain stores DetailsJson as an
/// opaque string, so the only contract that matters here is determinism + the exact shapes
/// downstream consumers parse back.
/// </summary>
public class AuditDetailsTests
{
    [Fact]
    public void Output_IsCompact_NoIndentation_DeclarationOrder()
    {
        // Property order must follow declaration order and output must be compact — the same
        // payload must always serialize to the same byte sequence (chain rows are evidence).
        var json = AuditDetails.Json(new { kekId = "k1", dekVersion = 3 });
        Assert.Equal("""{"kekId":"k1","dekVersion":3}""", json);
    }

    [Fact]
    public void Output_IsDeterministic_AcrossCalls()
    {
        var a = AuditDetails.Json(new { prunedThroughSeq = 42L, prunedThroughHash = "AB12" });
        var b = AuditDetails.Json(new { prunedThroughSeq = 42L, prunedThroughHash = "AB12" });
        Assert.Equal(a, b);
    }

    [Fact]
    public void PruneCheckpoint_Shape_RoundTripsThroughVerifierParsing()
    {
        // AuditVerifier.ResolveSeedAsync parses these exact property names back out of the
        // checkpoint row — the serializer change must not rename or re-case them.
        var json = AuditDetails.Json(new { prunedThroughSeq = 7L, prunedThroughHash = "0A0B" });
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(7L, doc.RootElement.GetProperty("prunedThroughSeq").GetInt64());
        Assert.Equal("0A0B", doc.RootElement.GetProperty("prunedThroughHash").GetString());
    }

    [Fact]
    public void SpecialCharacters_AreEscaped_NotInjected()
    {
        // The old hand-rolled interpolation would have produced malformed JSON here; the
        // serializer must always emit valid JSON regardless of the value content.
        var json = AuditDetails.Json(new { scope = "full\"},{\"oops\":\"x" });
        using var doc = System.Text.Json.JsonDocument.Parse(json); // throws if malformed
        Assert.Equal("full\"},{\"oops\":\"x", doc.RootElement.GetProperty("scope").GetString());
    }
}
