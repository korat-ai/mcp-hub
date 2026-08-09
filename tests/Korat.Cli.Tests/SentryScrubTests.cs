using Korat.Cli.Telemetry;
using Sentry;
using Sentry.Protocol;

namespace Korat.Cli.Tests;

/// <summary>
/// Tests the CLI telemetry scrubber. Two layers:
/// <list type="bullet">
///   <item><see cref="SentryInit.ScrubString"/> — the redaction primitives.</item>
///   <item><see cref="SentryInit.ScrubEvent"/> — the <c>BeforeSend</c> callback that
///   must actually apply the scrubber to every text surface. This is the regression
///   guard: a prior version wired BeforeSend but only nulled the server name, so
///   exception messages/breadcrumbs shipped unscrubbed.</item>
/// </list>
/// </summary>
public class SentryScrubTests
{
    private const string FakeHome = "/Users/somebody";

    // ---- ScrubString primitives ----------------------------------------

    [Fact]
    public void ScrubString_replaces_home_paths_with_tilde()
    {
        var input = $"failed to read {FakeHome}/.korat/config.json";
        var result = SentryInit.ScrubString(input, FakeHome);
        Assert.DoesNotContain(FakeHome, result);
        Assert.Contains("~/.korat/config.json", result);
    }

    [Fact]
    public void ScrubString_redacts_credentials_marker()
    {
        var input = $"{FakeHome}/.korat/credentials had bad json";
        var result = SentryInit.ScrubString(input, FakeHome);
        Assert.Contains("<credentials-redacted>", result);
    }

    [Theory]
    [InlineData("Authorization: Bearer abc123def")]
    [InlineData("token=abc123def")]
    [InlineData("cli_token=abc123def")]
    [InlineData("KORAT_SENTRY_DSN=https://k@telemetry.example.com/3")]
    public void ScrubString_redacts_tokens(string input)
    {
        var result = SentryInit.ScrubString(input, FakeHome);
        Assert.DoesNotContain("abc123def", result);
        Assert.DoesNotContain("//k@errors", result);
        Assert.Contains("<redacted>", result);
    }

    [Fact]
    public void ScrubString_redacts_bare_dsn()
    {
        var input = "init failed for https://pub1ickey@telemetry.example.com/42 — bad project";
        var result = SentryInit.ScrubString(input, FakeHome);
        Assert.DoesNotContain("pub1ickey", result);
        Assert.Contains("<dsn-redacted>", result);
    }

    [Fact]
    public void ScrubString_redacts_emails()
    {
        var result = SentryInit.ScrubString("owner is jane.doe@example.com", FakeHome);
        Assert.DoesNotContain("jane.doe@example.com", result);
        Assert.Contains("<email-redacted>", result);
    }

    [Fact]
    public void ScrubString_passes_through_clean_text()
    {
        const string clean = "connection refused: server bridge closed";
        Assert.Equal(clean, SentryInit.ScrubString(clean, FakeHome));
    }

    // ---- ScrubEvent wiring (the BLOCKER regression guard) ----------------

    [Fact]
    public void ScrubEvent_scrubs_message_and_exception()
    {
        // Build under the REAL home so the in-event $HOME redaction (which uses the
        // process home, not an injected one) is deterministic.
        var home = Environment.GetEnvironmentVariable("HOME")
                   ?? Environment.GetEnvironmentVariable("USERPROFILE")
                   ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var secretPath = $"{home}/.korat/credentials";
        const string email = "leak@example.com";
        const string token = "Bearer s3cr3ttoken";

        var ev = new SentryEvent
        {
            Message = new SentryMessage
            {
                Message = $"login failed for {email} reading {secretPath}",
                Formatted = $"login failed for {email} ({token})",
            },
            SentryExceptions =
            [
                new SentryException { Value = $"IOException at {secretPath} with {token}" },
            ],
            ServerName = "my-laptop.local",
        };

        var result = SentryInit.ScrubEvent(ev);

        Assert.NotNull(result);
        // Server name (identifies the machine) is dropped.
        Assert.Null(result!.ServerName);

        // Message surfaces scrubbed.
        Assert.DoesNotContain(email, result.Message!.Message!);
        Assert.DoesNotContain(home, result.Message.Message!);
        Assert.DoesNotContain("s3cr3ttoken", result.Message.Formatted!);

        // Exception value scrubbed.
        var exValue = result.SentryExceptions!.Single().Value!;
        Assert.DoesNotContain("s3cr3ttoken", exValue);
        Assert.DoesNotContain(home, exValue);
        Assert.Contains("<credentials-redacted>", exValue);
    }

    // ---- Transient-transport noise filter --------------------------------

    [Fact]
    public void ScrubEvent_drops_transient_transport_unobserved_task_noise()
    {
        // A runtime's relay stream dropped → unobserved
        // AggregateException(SocketException / IOException / RpcException Unavailable).
        var ev = new SentryEvent
        {
            SentryExceptions =
            [
                new SentryException { Type = "System.AggregateException", Value = "A Task's exception(s) were not observed" },
                new SentryException { Type = "Grpc.Core.RpcException", Value = "Status(StatusCode=\"Unavailable\", Detail=\"Error reading next message\")" },
                new SentryException { Type = "System.IO.IOException", Value = "The request was aborted." },
                new SentryException { Type = "System.Net.Sockets.SocketException", Value = "Operation timed out" },
            ],
        };

        Assert.True(SentryInit.IsTransientTransportNoise(ev));
        Assert.Null(SentryInit.ScrubEvent(ev)); // dropped — never shipped to GlitchTip
    }

    [Fact]
    public void ScrubEvent_keeps_unobserved_task_with_a_real_leaf()
    {
        // Same unobserved-task wrapper, but a genuine bug leaf → must NOT be dropped.
        var ev = new SentryEvent
        {
            SentryExceptions =
            [
                new SentryException { Type = "System.AggregateException", Value = "A Task's exception(s) were not observed" },
                new SentryException { Type = "System.NullReferenceException", Value = "Object reference not set to an instance of an object." },
            ],
        };

        Assert.False(SentryInit.IsTransientTransportNoise(ev));
        Assert.NotNull(SentryInit.ScrubEvent(ev));
    }

    [Fact]
    public void ScrubEvent_keeps_rpcexception_with_nontransient_status()
    {
        // A real gRPC error (PermissionDenied) must surface even as unobserved noise.
        var ev = new SentryEvent
        {
            SentryExceptions =
            [
                new SentryException { Type = "System.AggregateException", Value = "A Task's exception(s) were not observed" },
                new SentryException { Type = "Grpc.Core.RpcException", Value = "Status(StatusCode=\"PermissionDenied\")" },
            ],
        };

        Assert.False(SentryInit.IsTransientTransportNoise(ev));
        Assert.NotNull(SentryInit.ScrubEvent(ev));
    }

    [Fact]
    public void ScrubEvent_keeps_rpcexception_whose_detail_contains_a_transient_word()
    {
        // Regression guard: a REAL PermissionDenied whose server-supplied Detail happens
        // to contain "Cancelled" must NOT be misclassified as transient and dropped.
        // (The status match anchors on StatusCode="X", not a bare substring of the value.)
        var ev = new SentryEvent
        {
            SentryExceptions =
            [
                new SentryException { Type = "System.AggregateException", Value = "A Task's exception(s) were not observed" },
                new SentryException { Type = "Grpc.Core.RpcException", Value = "Status(StatusCode=\"PermissionDenied\", Detail=\"request was Cancelled by policy\")" },
            ],
        };

        Assert.False(SentryInit.IsTransientTransportNoise(ev));
        Assert.NotNull(SentryInit.ScrubEvent(ev));
    }

    [Fact]
    public void ScrubEvent_keeps_plain_transport_error_without_unobserved_wrapper()
    {
        // A bare SocketException with no AggregateException wrapper is NOT the
        // unobserved-task shape — keep it (could be a genuinely surfaced failure).
        var ev = new SentryEvent
        {
            SentryExceptions =
            [
                new SentryException { Type = "System.Net.Sockets.SocketException", Value = "Operation timed out" },
            ],
        };

        Assert.False(SentryInit.IsTransientTransportNoise(ev));
        Assert.NotNull(SentryInit.ScrubEvent(ev));
    }

    [Fact]
    public void ScrubBreadcrumb_rebuilds_with_scrubbed_message_and_data()
    {
        var crumb = new Breadcrumb(
            message: "ran command with Bearer s3cr3ttoken",
            type: "default",
            data: new Dictionary<string, string> { ["arg"] = "leak@example.com" },
            category: "cli");

        var result = SentryInit.ScrubBreadcrumb(crumb);

        Assert.NotNull(result);
        Assert.DoesNotContain("s3cr3ttoken", result!.Message!);
        Assert.DoesNotContain("leak@example.com", result.Data!["arg"]);
        // Non-secret metadata is preserved.
        Assert.Equal("default", result.Type);
        Assert.Equal("cli", result.Category);
    }
}
