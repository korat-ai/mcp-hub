using Korat.Cloud;
using NATS.Client.Core;

namespace Korat.Auth.Tests;

/// <summary>
/// Unit tests for <see cref="NatsUrl.ToOpts"/> — 009-nats-relay-backplane.
/// </summary>
public class NatsUrlTests
{
    [Fact]
    public void PlainNats_DisablesTls()
    {
        var opts = NatsUrl.ToOpts("nats://korat-nats.internal:4222", "korat-cloud");

        Assert.Equal("nats://korat-nats.internal:4222", opts.Url);
        Assert.Equal("korat-cloud", opts.Name);
        Assert.NotNull(opts.TlsOpts);
        Assert.Equal(TlsMode.Disable, opts.TlsOpts.Mode);
    }

    [Fact]
    public void TlsScheme_RequiresTls()
    {
        var opts = NatsUrl.ToOpts("tls://nats.example.com:4222", "korat-cloud");

        Assert.Equal(TlsMode.Require, opts.TlsOpts.Mode);
    }

    [Fact]
    public void TlsScheme_IsCaseInsensitive()
    {
        var opts = NatsUrl.ToOpts("TLS://nats.example.com:4222", "korat-cloud");

        Assert.Equal(TlsMode.Require, opts.TlsOpts.Mode);
    }

    [Fact]
    public void TrimsWhitespace()
    {
        var opts = NatsUrl.ToOpts("  nats://host:4222  ", "korat-cloud");

        Assert.Equal("nats://host:4222", opts.Url);
    }

    [Fact]
    public void ConfiguresResilientReconnect()
    {
        var opts = NatsUrl.ToOpts("nats://host:4222", "korat-cloud");

        Assert.True(opts.RetryOnInitialConnect);
        Assert.Equal(-1, opts.MaxReconnectRetry);
    }
}
