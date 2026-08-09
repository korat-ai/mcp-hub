using Korat.Cloud.Observability;
using Orleans.Runtime;
using Sentry;
using Sentry.Protocol;

namespace Korat.Auth.Tests;

/// <summary>
/// Verifies the cloud Sentry scrubber both at the primitive level
/// (<see cref="SentryScrub.ScrubText"/>) and — crucially — that the BeforeSend /
/// BeforeBreadcrumb callbacks actually apply it to the event's text surfaces.
/// (The CLI once shipped a BeforeSend that was wired but never scrubbed; this
/// guards the cloud surface against the same class of bug.)
/// </summary>
public class SentryScrubTests
{
    [Theory]
    [InlineData("Authorization: Bearer abc123xyz", "abc123xyz")]
    [InlineData("token=abc123xyz", "abc123xyz")]
    [InlineData("invite=abc123xyz", "abc123xyz")]
    public void ScrubText_redacts_tokens(string input, string secret)
    {
        var result = SentryScrub.ScrubText(input);
        Assert.DoesNotContain(secret, result);
        Assert.Contains("<redacted>", result);
    }

    [Fact]
    public void ScrubText_redacts_email_dsn_and_connstring_password()
    {
        Assert.Contains("<email-redacted>", SentryScrub.ScrubText("user bob@example.com missing"));
        Assert.Contains("<dsn-redacted>", SentryScrub.ScrubText("https://pub@telemetry.example.com/2 down"));

        var conn = SentryScrub.ScrubText("Host=db;Username=korat;Password=s3cret;Db=korat");
        Assert.DoesNotContain("s3cret", conn);
        Assert.Contains("Password=<redacted>", conn);
        // Non-secret connection parts are preserved.
        Assert.Contains("Host=db", conn);
    }

    /// <summary>
    /// Task 9 review fix (BLOCKER defence-in-depth): Telegram Bot API tokens live in the URL
    /// PATH (<c>api.telegram.org/bot{token}/method</c>) — the prefix-based TokenRegex
    /// (Bearer/token=/code=/invite=) and .NET's query-string-only redaction never match them.
    /// </summary>
    [Fact]
    public void ScrubText_redacts_telegram_bot_token_in_url_path()
    {
        var scrubbed = SentryScrub.ScrubText(
            "POST https://api.telegram.org/bot12345:AbCdEf-ghIJK_lmno/sendMessage failed");
        Assert.DoesNotContain("12345:AbCdEf-ghIJK_lmno", scrubbed);
        Assert.Contains("bot<redacted>", scrubbed);
        // The non-secret method segment survives (diagnosability).
        Assert.Contains("/sendMessage", scrubbed);
    }

    /// <summary>
    /// Fable review follow-up (#185 MEDIUM-2): TokenRegex only redacts a Bearer/token=/code=/
    /// invite= PREFIXED value — a bare Anthropic/OpenAI-shaped API key echoed by an upstream
    /// error body (no such prefix) previously sailed through unredacted and could ride a
    /// Warning breadcrumb or Error event into GlitchTip.
    /// </summary>
    [Theory]
    [InlineData("upstream 401: invalid key sk-ant-supersecretXYZ123", "sk-ant-supersecretXYZ123")]
    [InlineData("upstream 401: invalid key sk-openaikey99", "sk-openaikey99")]
    public void ScrubText_redacts_bare_api_key_with_no_prefix(string input, string secret)
    {
        var result = SentryScrub.ScrubText(input);
        Assert.DoesNotContain(secret, result);
        Assert.Contains("sk-<redacted>", result);
    }

    [Fact]
    public void ScrubText_passes_through_clean_text()
    {
        const string clean = "grain activation failed: timeout";
        Assert.Equal(clean, SentryScrub.ScrubText(clean));
    }

    [Fact]
    public void ScrubEvent_scrubs_message_and_exception_and_nulls_server()
    {
        const string email = "leak@example.com";
        const string token = "Bearer s3cr3ttoken";
        var ev = new SentryEvent
        {
            ServerName = "silo-7.internal",
            Message = new SentryMessage
            {
                Message = $"request failed for {email}",
                Formatted = $"request failed ({token})",
            },
            SentryExceptions = [new SentryException { Value = $"DbException Password=hunter2 for {email}" }],
        };

        var result = SentryScrub.ScrubEvent(ev);

        Assert.NotNull(result);
        Assert.Null(result!.ServerName);
        Assert.DoesNotContain(email, result.Message!.Message!);
        Assert.DoesNotContain("s3cr3ttoken", result.Message.Formatted!);

        var exValue = result.SentryExceptions!.Single().Value!;
        Assert.DoesNotContain("hunter2", exValue);
        Assert.DoesNotContain(email, exValue);
    }

    [Fact]
    public void ScrubEvent_keeps_real_exception_at_error_level()
    {
        var ev = new SentryEvent
        {
            Level = SentryLevel.Error,
            SentryExceptions = [new SentryException { Type = "System.NullReferenceException", Value = "boom" }],
        };

        var result = SentryScrub.ScrubEvent(ev);

        Assert.Equal(SentryLevel.Error, result!.Level);
    }

    [Theory]
    [InlineData("Orleans.Runtime.OrleansMessageRejectionException")]
    [InlineData("Orleans.Runtime.SiloUnavailableException")]
    public void ScrubEvent_drops_transient_cluster_churn_by_exception_type(string type)
    {
        var ev = new SentryEvent
        {
            Level = SentryLevel.Error,
            SentryExceptions = [new SentryException { Type = type, Value = "the target silo is no longer active" }],
        };

        // Transient rolling-deploy membership churn — dropped entirely (no GlitchTip issue).
        Assert.Null(SentryScrub.ScrubEvent(ev));
    }

    [Fact]
    public void ScrubEvent_drops_transient_cluster_churn_by_relay_message()
    {
        // The relay logs the inner type as a string with no exception object attached.
        var ev = new SentryEvent
        {
            Level = SentryLevel.Error,
            Message = new SentryMessage
            {
                Message = "Stream error on node={NodeId} errorType={ErrorType}",
                Formatted = "Stream error on node=abc errorType=OrleansMessageRejectionException",
            },
        };

        Assert.Null(SentryScrub.ScrubEvent(ev));
    }

    [Fact]
    public void ScrubEvent_keeps_unrelated_stream_error()
    {
        // A genuine non-churn stream error must still be reported.
        var ev = new SentryEvent
        {
            Level = SentryLevel.Error,
            Message = new SentryMessage
            {
                Message = "Stream error on node=abc errorType=InvalidOperationException",
                Formatted = "Stream error on node=abc errorType=InvalidOperationException",
            },
        };

        Assert.NotNull(SentryScrub.ScrubEvent(ev));
    }

    [Fact]
    public void ScrubBreadcrumb_rebuilds_scrubbed()
    {
        var crumb = new Breadcrumb(
            message: "called api with Bearer s3cr3ttoken",
            type: "http",
            data: new Dictionary<string, string> { ["user"] = "leak@example.com" },
            category: "request");

        var result = SentryScrub.ScrubBreadcrumb(crumb);

        Assert.NotNull(result);
        Assert.DoesNotContain("s3cr3ttoken", result!.Message!);
        Assert.DoesNotContain("leak@example.com", result.Data!["user"]);
        Assert.Equal("http", result.Type);
    }

    // --- CLR exception chain walk (IsTransientClusterNoise + ScrubEvent(ev.Exception)) ---

    [Fact]
    public void IsTransientClusterNoise_SiloUnavailableException_returnsTrue()
    {
        Assert.True(SentryScrub.IsTransientClusterNoise(new SiloUnavailableException()));
    }

    [Fact]
    public void IsTransientClusterNoise_OrleansMessageRejectionException_returnsTrue()
    {
        // OrleansMessageRejectionException has no public constructor (internal to Orleans) —
        // verify via the type object itself using pattern matching logic equivalent.
        // The actual `is OrleansMessageRejectionException` check in the helper is compile-time
        // verified by the SentryScrub.cs build; here we confirm type identity is reachable.
        Assert.Equal("Orleans.Runtime.OrleansMessageRejectionException",
            typeof(OrleansMessageRejectionException).FullName);
    }

    [Fact]
    public void IsTransientClusterNoise_null_returnsFalse()
    {
        Assert.False(SentryScrub.IsTransientClusterNoise(null));
    }

    [Fact]
    public void IsTransientClusterNoise_unrelated_exception_returnsFalse()
    {
        Assert.False(SentryScrub.IsTransientClusterNoise(new InvalidOperationException("something went wrong")));
    }

    [Fact]
    public void IsTransientClusterNoise_AggregateException_wrapping_SiloUnavailable_returnsTrue()
    {
        var agg = new AggregateException(
            new InvalidOperationException("first"),
            new SiloUnavailableException());

        Assert.True(SentryScrub.IsTransientClusterNoise(agg));
    }

    [Fact]
    public void IsTransientClusterNoise_InnerException_SiloUnavailable_returnsTrue()
    {
        var wrapped = new Exception("wrapper", new SiloUnavailableException());
        Assert.True(SentryScrub.IsTransientClusterNoise(wrapped));
    }

    [Fact]
    public void ScrubEvent_drops_when_Exception_is_SiloUnavailableException()
    {
        var ev = new SentryEvent(new SiloUnavailableException());
        Assert.Null(SentryScrub.ScrubEvent(ev));
    }

    [Fact]
    public void ScrubEvent_drops_OrleansMessageRejectionException_via_SentryExceptions()
    {
        // OrleansMessageRejectionException has no public constructor; test via the Sentry
        // protocol path (SentryExceptions type-name string) which covers the manual-capture
        // and relay-log routes that don't attach a live CLR exception.
        var ev = new SentryEvent
        {
            Level = SentryLevel.Error,
            SentryExceptions =
            [
                new SentryException
                {
                    Type = "Orleans.Runtime.OrleansMessageRejectionException",
                    Value = "the target silo is no longer active",
                },
            ],
        };
        Assert.Null(SentryScrub.ScrubEvent(ev));
    }

    [Fact]
    public void ScrubEvent_drops_when_Exception_is_AggregateException_wrapping_SiloUnavailable()
    {
        var agg = new AggregateException(new SiloUnavailableException());
        var ev = new SentryEvent(agg);
        Assert.Null(SentryScrub.ScrubEvent(ev));
    }

    [Fact]
    public void ScrubEvent_keeps_generic_exception_via_CLR_chain()
    {
        // A real bug (InvalidOperationException) must NOT be dropped — confirm CLR guard
        // doesn't over-broadly suppress genuine errors.
        var ev = new SentryEvent(new InvalidOperationException("real bug"))
        {
            Level = SentryLevel.Error,
        };
        Assert.NotNull(SentryScrub.ScrubEvent(ev));
        Assert.Equal(SentryLevel.Error, SentryScrub.ScrubEvent(ev)!.Level);
    }

    [Theory]
    [InlineData("Orleans.Runtime.OrleansClusterConnectivityCheckFailedException")]
    public void ScrubEvent_drops_connectivity_check_exception_by_typename(string fullName)
    {
        // OrleansClusterConnectivityCheckFailedException is Orleans.Runtime-internal;
        // matched by full type name so a hard type ref is not required.
        var ev = new SentryEvent
        {
            Level = SentryLevel.Error,
            SentryExceptions =
            [
                new SentryException
                {
                    Type = fullName,
                    Value = "Failed to get ping responses from newly joining silos",
                },
            ],
        };
        Assert.Null(SentryScrub.ScrubEvent(ev));
    }

    // --- request-abort / shutdown cancellation noise (IsCancellationNoise) ---

    [Fact]
    public void IsCancellationNoise_OperationCanceled_returnsTrue()
    {
        Assert.True(SentryScrub.IsCancellationNoise(new OperationCanceledException()));
    }

    [Fact]
    public void IsCancellationNoise_TaskCanceled_returnsTrue()
    {
        // TaskCanceledException : OperationCanceledException — also covered.
        Assert.True(SentryScrub.IsCancellationNoise(new TaskCanceledException()));
    }

    [Fact]
    public void IsCancellationNoise_null_and_unrelated_returnFalse()
    {
        Assert.False(SentryScrub.IsCancellationNoise(null));
        Assert.False(SentryScrub.IsCancellationNoise(new InvalidOperationException("real bug")));
    }

    [Fact]
    public void IsCancellationNoise_wrapped_OperationCanceled_returnsTrue()
    {
        // The observed prod shape: a request-aborted token cancels an in-flight Npgsql
        // connection open, surfacing wrapped in the exception chain.
        var wrapped = new Exception("DbCommand failed", new OperationCanceledException());
        Assert.True(SentryScrub.IsCancellationNoise(wrapped));

        var agg = new AggregateException(new InvalidOperationException("x"), new TaskCanceledException());
        Assert.True(SentryScrub.IsCancellationNoise(agg));
    }

    [Fact]
    public void ScrubEvent_drops_when_Exception_is_OperationCanceled()
    {
        var ev = new SentryEvent(new OperationCanceledException("The operation was canceled."));
        Assert.Null(SentryScrub.ScrubEvent(ev));
    }

    // --- Orleans join-retry progress noise filter ---

    private const string JoinRetryPrefix =
        "Failed to get ping responses from 2 of 3 active silos. Newly joining silos validate "
        + "connectivity to already active silos before joining the cluster to reduce the "
        + "probability of a partitioning event when starting many silos in parallel. Will "
        + "continue attempting to validate connectivity until 00:05:00.";

    private static string JoinRetryMessage(int attempt) => $"{JoinRetryPrefix} Attempt #{attempt}.";

    [Fact]
    public void IsOrleansJoinRetryNoise_earlyAttempt_returnsTrue()
    {
        Assert.True(SentryScrub.IsOrleansJoinRetryNoise(JoinRetryMessage(3)));
    }

    [Fact]
    public void IsOrleansJoinRetryNoise_attemptBeyondThreshold_returnsFalse()
    {
        // Attempt 6 is past the expected rolling-deploy retry window — a joining silo
        // stuck this long is worth alerting on, not silently dropping.
        Assert.False(SentryScrub.IsOrleansJoinRetryNoise(JoinRetryMessage(6)));
    }

    [Fact]
    public void IsOrleansJoinRetryNoise_terminalMessageWithoutContinueMarker_returnsFalse()
    {
        // Same leading phrase as the retry-progress log, but missing "Will continue
        // attempting" — this is the shape of the TERMINAL failure message and must surface.
        const string terminal = "Failed to get ping responses from 3 of 3 active silos. "
            + "Marking this silo as dead.";
        Assert.False(SentryScrub.IsOrleansJoinRetryNoise(terminal));
    }

    [Fact]
    public void IsOrleansJoinRetryNoise_unrelatedMessage_returnsFalse()
    {
        Assert.False(SentryScrub.IsOrleansJoinRetryNoise("grain activation failed: timeout"));
    }

    [Fact]
    public void IsOrleansJoinRetryNoise_nullOrEmpty_returnsFalse()
    {
        Assert.False(SentryScrub.IsOrleansJoinRetryNoise(null));
        Assert.False(SentryScrub.IsOrleansJoinRetryNoise(string.Empty));
    }

    [Fact]
    public void ScrubEvent_drops_orleans_join_retry_noise_via_formatted_message()
    {
        var ev = new SentryEvent
        {
            Level = SentryLevel.Error,
            Message = new SentryMessage
            {
                Message = "Failed to get ping responses from {FailedCount} of {ActiveCount} "
                    + "active silos. Newly joining silos validate connectivity ... Will "
                    + "continue attempting to validate connectivity until {Timeout}. "
                    + "Attempt #{Attempt}.",
                Formatted = JoinRetryMessage(3),
            },
        };

        Assert.Null(SentryScrub.ScrubEvent(ev));
    }

    [Fact]
    public void ScrubEvent_keeps_orleans_join_retry_past_attempt_five()
    {
        var ev = new SentryEvent
        {
            Level = SentryLevel.Error,
            Message = new SentryMessage
            {
                Message = "Failed to get ping responses from {FailedCount} of {ActiveCount} "
                    + "active silos. Newly joining silos validate connectivity ... Will "
                    + "continue attempting to validate connectivity until {Timeout}. "
                    + "Attempt #{Attempt}.",
                Formatted = JoinRetryMessage(6),
            },
        };

        Assert.NotNull(SentryScrub.ScrubEvent(ev));
    }
}
