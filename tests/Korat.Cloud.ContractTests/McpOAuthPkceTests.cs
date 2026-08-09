using System.Security.Cryptography;
using System.Text;
using Korat.Cloud.Mcp.Oauth;
using Xunit;

namespace Korat.Cloud.ContractTests;

public sealed class McpOAuthPkceTests
{
    [Fact]
    public void GenerateVerifier_ProducesRfc7636CompliantLength()
    {
        var verifier = McpOAuthPkce.GenerateVerifier();
        Assert.InRange(verifier.Length, 43, 128); // RFC 7636 §4.1
        Assert.DoesNotContain('=', verifier);
        Assert.DoesNotContain('+', verifier);
        Assert.DoesNotContain('/', verifier);
    }

    [Fact]
    public void Challenge_MatchesManualS256Computation()
    {
        var verifier = McpOAuthPkce.GenerateVerifier();
        var expected = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.Equal(expected, McpOAuthPkce.Challenge(verifier));
    }

    [Fact]
    public void GenerateState_ProducesDistinctHighEntropyValues()
    {
        var a = McpOAuthPkce.GenerateState();
        var b = McpOAuthPkce.GenerateState();
        Assert.NotEqual(a, b);
        Assert.True(a.Length >= 32);
    }

    [Fact]
    public void BuildAuthorizeUrl_ComposesAllRequiredParameters()
    {
        var url = McpOAuthPkce.BuildAuthorizeUrl(
            "https://as.example.test/authorize", "client-1", "https://cloud.korat.test/api/mcp/oauth/callback/srv-1",
            "state-xyz", "challenge-abc", "https://mcp.example.test/");

        var uri = new Uri(url);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("client-1", query["client_id"]);
        Assert.Equal("https://cloud.korat.test/api/mcp/oauth/callback/srv-1", query["redirect_uri"]);
        Assert.Equal("state-xyz", query["state"]);
        Assert.Equal("challenge-abc", query["code_challenge"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("https://mcp.example.test/", query["resource"]);
    }

    [Fact]
    public void BuildAuthorizeUrl_PreservesAnExistingQueryString_NoDoubleQuestionMark()
    {
        // Some ASes' authorization_endpoint already carries a query string (e.g. "?tenant=acme") —
        // naive string concatenation with "?" would produce an invalid "...&?..." / double "?" URL.
        var url = McpOAuthPkce.BuildAuthorizeUrl(
            "https://as.example.test/authorize?tenant=acme", "client-1", "https://cloud.korat.test/cb",
            "state-xyz", "challenge-abc", "https://mcp.example.test/");

        var uri = new Uri(url);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        Assert.Equal("acme", query["tenant"]); // original param preserved
        Assert.Equal("code", query["response_type"]); // new params added correctly
        // Plan Step 5 note: the literal Assert.Single(url.Split('?')) in the plan's reference is
        // wrong as written (a well-formed URL with one query string splits into 2 elements) —
        // corrected here per the plan's own inline note.
        Assert.Equal(2, url.Split('?').Length);
    }
}
