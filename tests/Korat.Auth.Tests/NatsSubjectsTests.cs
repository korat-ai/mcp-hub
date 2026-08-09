using Korat.Cloud.Gateways;
using Korat.Domain;

namespace Korat.Auth.Tests;

/// <summary>
/// Unit tests for <see cref="NatsSubjects"/> — 009-nats-relay-backplane.
/// The encoding must keep subjects valid AND prevent wildcard/subject injection from a
/// hostile NodeId.
/// </summary>
public class NatsSubjectsTests
{
    [Fact]
    public void Frame_HasExpectedPrefix()
    {
        var subject = NatsSubjects.Frame(new NodeId("node-1"));

        Assert.StartsWith("korat.relay.frame.", subject);
    }

    [Fact]
    public void Frame_IsDeterministic()
    {
        var a = NatsSubjects.Frame(new NodeId("node-1"));
        var b = NatsSubjects.Frame(new NodeId("node-1"));

        Assert.Equal(a, b);
    }

    [Fact]
    public void Frame_DistinctNodesDistinctSubjects()
    {
        var a = NatsSubjects.Frame(new NodeId("node-1"));
        var b = NatsSubjects.Frame(new NodeId("node-2"));

        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("with.dots.everywhere")]
    [InlineData("wild>card")]
    [InlineData("star*token")]
    [InlineData("has space")]
    [InlineData("emoji🚀node")]
    public void Frame_EncodedTokenHasNoNatsSpecialChars(string rawNodeId)
    {
        var subject = NatsSubjects.Frame(new NodeId(rawNodeId));

        // The single encoded token (everything after the fixed prefix) must not contain any
        // character that NATS treats specially or rejects.
        var token = subject[NatsSubjects.FramePrefix.Length..];
        Assert.DoesNotContain('.', token);
        Assert.DoesNotContain(' ', token);
        Assert.DoesNotContain('*', token);
        Assert.DoesNotContain('>', token);
        Assert.DoesNotContain('/', token); // base64 '/' must be url-safe-substituted
        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('=', token); // padding stripped
    }

    // ── 029 / M2: inference backplane subject (korat.relay.inf.) ─────────────
    // Subject now includes the owning node id for sender isolation.

    private static readonly NodeId TestNode = new(Guid.NewGuid().ToString("N"));

    [Fact]
    public void Inf_HasExpectedPrefix()
    {
        var subject = NatsSubjects.Inf("corr-abc123", TestNode);
        Assert.StartsWith(NatsSubjects.InfPrefix, subject);
    }

    [Fact]
    public void Inf_IsDeterministic()
    {
        var a = NatsSubjects.Inf("corr-abc", TestNode);
        var b = NatsSubjects.Inf("corr-abc", TestNode);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Inf_DistinctCorrIdsDistinctSubjects()
    {
        var a = NatsSubjects.Inf("corr-001", TestNode);
        var b = NatsSubjects.Inf("corr-002", TestNode);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Inf_DistinctNodeIdsDistinctSubjects()
    {
        // M2: same corrId, different owning nodes → different subjects.
        var nodeA = new NodeId(Guid.NewGuid().ToString("N"));
        var nodeB = new NodeId(Guid.NewGuid().ToString("N"));
        var a = NatsSubjects.Inf("corr-same", nodeA);
        var b = NatsSubjects.Inf("corr-same", nodeB);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Inf_DoesNotAliasFrameOrConnSubjects()
    {
        // The inference prefix must be distinct from all other prefix constants so a
        // malicious corrId cannot alias a node or connection inbox.
        Assert.NotEqual(NatsSubjects.InfPrefix, NatsSubjects.FramePrefix);
        Assert.NotEqual(NatsSubjects.InfPrefix, NatsSubjects.ConnPrefix);
        Assert.NotEqual(NatsSubjects.InfPrefix, NatsSubjects.TapPrefix);

        // Even with the same encoded value, the subject is distinct.
        var fakeNodeId = "aGVsbG8"; // base64url of "hello"
        var infSubject = NatsSubjects.InfPrefix + fakeNodeId;
        var frameSubject = NatsSubjects.FramePrefix + fakeNodeId;
        Assert.NotEqual(infSubject, frameSubject);
    }

    [Theory]
    [InlineData("plain-guid")]
    [InlineData("AAAA-BBBB")]
    [InlineData("has.dots")]
    [InlineData("wild>card")]
    [InlineData("star*id")]
    public void Inf_EncodedTokenHasNoNatsSpecialChars(string corrId)
    {
        var subject = NatsSubjects.Inf(corrId, TestNode);
        // Only check that NATS-illegal chars aren't present anywhere after the prefix.
        // The subject now has two encoded tokens separated by a '.', which is legal
        // in NATS subjects as a token separator (each token is still safe).
        var afterPrefix = subject[NatsSubjects.InfPrefix.Length..];
        Assert.DoesNotContain(' ', afterPrefix);
        Assert.DoesNotContain('*', afterPrefix);
        Assert.DoesNotContain('>', afterPrefix);
        Assert.DoesNotContain('/', afterPrefix);
        Assert.DoesNotContain('+', afterPrefix);
        Assert.DoesNotContain('=', afterPrefix);
    }
}
