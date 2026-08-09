using Korat.Cli.Gateway;

namespace Korat.Cli.Tests;

/// <summary>
/// #104: locks the tiered E2E console contract — calm plain-language default, [e2e] protocol
/// detail only under --verbose, security-critical outcomes always shown.
/// </summary>
[Collection("Console state")]
public sealed class E2eConsoleTests : IDisposable
{
    private readonly TextWriter _originalErr = Console.Error;

    private string Capture(bool verbose, Action act)
    {
        var prev = E2eConsole.Verbose;
        E2eConsole.Verbose = verbose;
        var sw = new StringWriter();
        Console.SetError(sw);
        try { act(); } finally { Console.SetError(_originalErr); E2eConsole.Verbose = prev; }
        return sw.ToString();
    }

    public void Dispose() => Console.SetError(_originalErr);

    [Fact]
    public void Encrypted_Default_IsPlain_NoJargon()
    {
        var o = Capture(false, () => E2eConsole.Encrypted("sess-1"));
        Assert.Contains("Connection is end-to-end encrypted.", o);
        Assert.DoesNotContain("[e2e]", o);
        Assert.DoesNotContain("sess-1", o);
    }

    [Fact]
    public void Encrypted_Verbose_ShowsProtocolDetail()
    {
        var o = Capture(true, () => E2eConsole.Encrypted("sess-1"));
        Assert.Contains("[e2e]", o);
        Assert.Contains("sess-1", o);
    }

    [Fact]
    public void FellBackToPlaintext_Default_IsCalm_PointsAtRequire()
    {
        var o = Capture(false, () => E2eConsole.FellBackToPlaintext("s", "publisher does not support E2E"));
        Assert.Contains("Encryption unavailable", o);
        Assert.Contains("--e2e=require", o);
        Assert.DoesNotContain("[e2e]", o);
        Assert.DoesNotContain("publisher does not support", o); // technical reason hidden by default
    }

    [Fact]
    public void RequiredButUnavailable_AlwaysShown_EvenWhenNotVerbose()
    {
        var o = Capture(false, () => E2eConsole.RequiredButUnavailable("s", "handshake timed out"));
        Assert.Contains("connection closed (--e2e=require)", o);
    }

    [Fact]
    public void HandshakeFailedClosing_AlwaysShown_SignalsInterference()
    {
        var o = Capture(false, () => E2eConsole.HandshakeFailedClosing("s", "broken confirm tag"));
        Assert.Contains("connection closed", o);
        Assert.Contains("active interference", o);
        Assert.DoesNotContain("broken confirm tag", o); // detail only under verbose
    }

    [Fact]
    public void Detail_Default_PrintsNothing()
    {
        var o = Capture(false, () => E2eConsole.Detail("unsupported curve 'x'"));
        Assert.Equal("", o);
    }

    [Fact]
    public void Detail_Verbose_PrintsTagged()
    {
        var o = Capture(true, () => E2eConsole.Detail("unsupported curve 'x'"));
        Assert.Contains("[e2e]", o);
        Assert.Contains("unsupported curve", o);
    }

    [Fact]
    public void DowngradeAttackDetected_Default_IsLoud_NoEncJargon()
    {
        var o = Capture(false, () => E2eConsole.DowngradeAttackDetected("sess-2", enc: 0));
        Assert.Contains("connection closed", o);
        Assert.Contains("attack", o);
        Assert.DoesNotContain("[e2e]", o);
        Assert.DoesNotContain("sess-2", o);
        Assert.DoesNotContain("enc=", o);
    }

    [Fact]
    public void DowngradeAttackDetected_Verbose_ShowsDetail()
    {
        var o = Capture(true, () => E2eConsole.DowngradeAttackDetected("sess-2", enc: 0));
        Assert.Contains("[e2e]", o);
        Assert.Contains("sess-2", o);
        Assert.Contains("enc=0", o);
    }

    [Fact]
    public void EncCipherMismatch_Default_IsLoud_NoEncJargon()
    {
        var o = Capture(false, () => E2eConsole.EncCipherMismatch("sess-3", enc: 1, hasCipher: false));
        Assert.Contains("connection closed", o);
        Assert.DoesNotContain("[e2e]", o);
        Assert.DoesNotContain("sess-3", o);
        Assert.DoesNotContain("enc=", o);
    }

    [Fact]
    public void EncCipherMismatch_Verbose_ShowsDetail()
    {
        var o = Capture(true, () => E2eConsole.EncCipherMismatch("sess-3", enc: 1, hasCipher: false));
        Assert.Contains("[e2e]", o);
        Assert.Contains("sess-3", o);
        Assert.Contains("enc=1", o);
    }

    [Fact]
    public void RequireFailedForServer_AlwaysShown_NamesBothServer_And_Policy()
    {
        var o = Capture(false, () => E2eConsole.RequireFailedForServer("sess-4", "my-server"));
        Assert.Contains("my-server", o);
        Assert.Contains("--e2e=require", o);
        Assert.Contains("session closed", o);
    }

    [Fact]
    public void RequireFailedForServer_Verbose_ShowsDetail()
    {
        var o = Capture(true, () => E2eConsole.RequireFailedForServer("sess-4", "my-server"));
        Assert.Contains("[e2e]", o);
        Assert.Contains("sess-4", o);
    }
}
