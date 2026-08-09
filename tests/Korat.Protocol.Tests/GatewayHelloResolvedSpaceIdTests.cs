using Google.Protobuf;
using Korat.Relay.V1;

namespace Korat.Protocol.Tests;

/// <summary>
/// Proto back-compat and field-contract tests for the resolved_space_id field
/// added to GatewayHello (fix/default-space-placeholder).
///
/// Invariants tested:
///   (a) resolved_space_id is field number 5; existing fields 1-4 are unchanged.
///   (b) GatewayHello without resolved_space_id deserializes cleanly (old wire format
///       back-compat: a server not yet deployed still sends 4 fields; CLI must handle it).
///   (c) Server populates resolved_space_id and the CLI can read it.
///   (d) A GatewayHello serialized with resolved_space_id round-trips correctly.
/// </summary>
public class GatewayHelloResolvedSpaceIdTests
{
    // ── (a) Field number contract ────────────────────────────────────────────

    [Fact]
    public void GatewayHello_ResolvedSpaceId_IsFieldNumber5()
    {
        var descriptor = GatewayHello.Descriptor;
        var field = descriptor.FindFieldByName("resolved_space_id");
        Assert.NotNull(field);
        Assert.Equal(5, field.FieldNumber);
    }

    [Fact]
    public void GatewayHello_ExistingFields_HaveUnchangedFieldNumbers()
    {
        var d = GatewayHello.Descriptor;
        Assert.Equal(1, d.FindFieldByName("gateway_id").FieldNumber);
        Assert.Equal(2, d.FindFieldByName("connection_id").FieldNumber);
        Assert.Equal(3, d.FindFieldByName("current_cli_version").FieldNumber);
        Assert.Equal(4, d.FindFieldByName("min_supported_cli_version").FieldNumber);
    }

    // ── (b) Back-compat: old wire (no field 5) deserializes cleanly ──────────

    [Fact]
    public void GatewayHello_OldWireWithoutResolvedSpaceId_DeserializesWithEmptyField()
    {
        // Simulate an old server that sends only fields 1-4, no field 5.
        var old = new GatewayHello
        {
            GatewayId = "gw-1",
            ConnectionId = "conn-abc",
            CurrentCliVersion = "0.3.0",
            MinSupportedCliVersion = "0.2.0",
            // ResolvedSpaceId NOT set — old server did not know about it.
        };

        var bytes = old.ToByteArray();
        var parsed = GatewayHello.Parser.ParseFrom(bytes);

        Assert.Equal("gw-1", parsed.GatewayId);
        Assert.Equal("conn-abc", parsed.ConnectionId);
        Assert.Equal("0.3.0", parsed.CurrentCliVersion);
        Assert.Equal("0.2.0", parsed.MinSupportedCliVersion);
        // Field 5 is absent → proto3 default = empty string.
        Assert.Equal(string.Empty, parsed.ResolvedSpaceId);
    }

    // ── (c) Server sets resolved_space_id; CLI can read it ───────────────────

    [Fact]
    public void GatewayHello_WithResolvedSpaceId_IsAccessible()
    {
        var spaceId = Guid.NewGuid().ToString();
        var ack = new GatewayHello
        {
            GatewayId = "gw-2",
            ConnectionId = "conn-xyz",
            CurrentCliVersion = "0.3.1",
            MinSupportedCliVersion = "0.2.0",
            ResolvedSpaceId = spaceId,
        };

        Assert.Equal(spaceId, ack.ResolvedSpaceId);
    }

    // ── (d) Full round-trip ───────────────────────────────────────────────────

    [Fact]
    public void GatewayHello_ResolvedSpaceId_RoundTrips()
    {
        var spaceId = "11112222-3333-4444-5555-666677778888";
        var original = new GatewayHello
        {
            GatewayId = "gw-rt",
            ConnectionId = "conn-rt",
            CurrentCliVersion = "1.0.0",
            MinSupportedCliVersion = "0.3.0",
            ResolvedSpaceId = spaceId,
        };

        var bytes = original.ToByteArray();
        var parsed = GatewayHello.Parser.ParseFrom(bytes);

        Assert.Equal(spaceId, parsed.ResolvedSpaceId);
        Assert.Equal("gw-rt", parsed.GatewayId);
        Assert.Equal("conn-rt", parsed.ConnectionId);
    }
}
