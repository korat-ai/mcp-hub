using System.Text.Json.Nodes;
using Google.Protobuf;
using Korat.Cloud.Gateways;
using Korat.Cloud.Web.Spaces;
using Korat.Domain;
using Korat.Relay.V1;

namespace Korat.Auth.Tests;

// ── SECURITY MAJOR-1: PATCH must validate AuthHeaderName via forbidden-header blocklist ──────

/// <summary>
/// SECURITY MAJOR-1: OutboundInferenceValidation.ValidateByoEndpoint must reject
/// forbidden headers (Host, Content-Length, Transfer-Encoding, Connection, etc.).
/// Before the fix, PATCH forwarded AuthHeaderName without running this validation.
/// These tests verify that the domain validation (called from both POST and PATCH)
/// correctly blocks forbidden headers so the PATCH path gains the same protection.
/// </summary>
public class PatchAuthHeaderValidation_SecurityMajor1_Tests
{
    [Theory]
    [InlineData("Host")]
    [InlineData("Content-Length")]
    [InlineData("Transfer-Encoding")]
    [InlineData("Connection")]
    [InlineData("Keep-Alive")]
    [InlineData("Upgrade")]
    [InlineData("Proxy-Authenticate")]
    [InlineData("Proxy-Authorization")]
    [InlineData("TE")]
    [InlineData("Trailer")]
    public void ForbiddenHeader_Returns_Error(string headerName)
    {
        // ValidateByoEndpoint is the shared validator — must reject forbidden headers.
        var err = OutboundInferenceValidation.ValidateByoEndpoint("https://placeholder.internal", headerName);
        Assert.NotNull(err);
        Assert.Contains("forbidden", err, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("X-Api-Key")]
    [InlineData("Authorization")]
    [InlineData("X-Custom-Auth")]
    [InlineData("Bearer-Token")]
    public void AllowedHeader_Returns_Null(string headerName)
    {
        var err = OutboundInferenceValidation.ValidateByoEndpoint("https://placeholder.internal", headerName);
        Assert.Null(err);
    }

    [Theory]
    [InlineData("has spaces")]
    [InlineData("has\ttab")]
    [InlineData("@invalid")]
    public void InvalidRfc7230_HeaderName_Returns_Error(string headerName)
    {
        var err = OutboundInferenceValidation.ValidateByoEndpoint("https://placeholder.internal", headerName);
        Assert.NotNull(err);
        Assert.Contains("RFC 7230", err, StringComparison.OrdinalIgnoreCase);
    }
}

