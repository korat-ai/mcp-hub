using System.Reflection;
using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Korat.Cloud.Push;
using Microsoft.Extensions.Logging.Abstractions;

namespace Korat.Cloud.IntegrationTests.Push;

public sealed class FcmAlertSenderTests
{
    private sealed class FakeFcmMessagingClient(Func<Message, Task<string>> impl) : IFcmMessagingClient
    {
        public Message? LastMessage { get; private set; }
        public Task<string> SendAsync(Message message, CancellationToken ct)
        {
            LastMessage = message;
            return impl(message);
        }
    }

    private static AlertContent SampleContent() => new(
        "New access request",
        "Agent \"cursor\" requests access to \"filesystem\"",
        new Dictionary<string, string> { ["type"] = "access_request", ["accessRequestId"] = "req-123" });

    /// <summary>
    /// FirebaseMessagingException's constructor is `internal` (FirebaseAdmin SDK) — not
    /// constructible directly from this assembly. Reflection bypasses the accessibility check
    /// (ConstructorInfo.Invoke ignores `internal` for NonPublic-flagged lookups), which is the
    /// only way to build a real instance for a deterministic unit test.
    /// </summary>
    private static FirebaseMessagingException MakeFcmException(MessagingErrorCode? fcmCode, FirebaseAdmin.ErrorCode baseCode = FirebaseAdmin.ErrorCode.Unknown)
    {
        var ctor = typeof(FirebaseMessagingException)
            .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(c => c.GetParameters().Length == 5);
        return (FirebaseMessagingException)ctor.Invoke(new object?[] { baseCode, "simulated", fcmCode, null, null });
    }

    [Fact]
    public async Task SendAlertAsync_Returns_Delivered_On_Success()
    {
        var client = new FakeFcmMessagingClient(_ => Task.FromResult("projects/x/messages/1"));
        var sender = new FcmAlertSender(client, NullLogger<FcmAlertSender>.Instance);

        var result = await sender.SendAlertAsync("fcm-token", "fcm", SampleContent(), CancellationToken.None);

        Assert.Equal(AlertSendResult.Delivered, result);
    }

    [Fact]
    public async Task SendAlertAsync_Returns_TokenInvalid_On_Unregistered()
    {
        var client = new FakeFcmMessagingClient(_ => throw MakeFcmException(MessagingErrorCode.Unregistered));
        var sender = new FcmAlertSender(client, NullLogger<FcmAlertSender>.Instance);

        var result = await sender.SendAlertAsync("fcm-token", "fcm", SampleContent(), CancellationToken.None);

        Assert.Equal(AlertSendResult.TokenInvalid, result);
    }

    [Fact]
    public async Task SendAlertAsync_Returns_TokenInvalid_On_Base_NotFound()
    {
        var client = new FakeFcmMessagingClient(_ => throw MakeFcmException(fcmCode: null, baseCode: FirebaseAdmin.ErrorCode.NotFound));
        var sender = new FcmAlertSender(client, NullLogger<FcmAlertSender>.Instance);

        var result = await sender.SendAlertAsync("fcm-token", "fcm", SampleContent(), CancellationToken.None);

        Assert.Equal(AlertSendResult.TokenInvalid, result);
    }

    [Fact]
    public async Task SendAlertAsync_Returns_TransientFailure_On_Other_FirebaseError()
    {
        var client = new FakeFcmMessagingClient(_ => throw MakeFcmException(MessagingErrorCode.Unavailable));
        var sender = new FcmAlertSender(client, NullLogger<FcmAlertSender>.Instance);

        var result = await sender.SendAlertAsync("fcm-token", "fcm", SampleContent(), CancellationToken.None);

        Assert.Equal(AlertSendResult.TransientFailure, result);
    }

    [Fact]
    public async Task SendAlertAsync_Returns_TransientFailure_On_Generic_Exception()
    {
        var client = new FakeFcmMessagingClient(_ => throw new InvalidOperationException("boom"));
        var sender = new FcmAlertSender(client, NullLogger<FcmAlertSender>.Instance);

        var result = await sender.SendAlertAsync("fcm-token", "fcm", SampleContent(), CancellationToken.None);

        Assert.Equal(AlertSendResult.TransientFailure, result);
    }

    [Fact]
    public async Task SendAlertAsync_Sends_DataOnly_Message_With_Title_Body_And_CollapseKey()
    {
        Message? captured = null;
        var client = new FakeFcmMessagingClient(m => { captured = m; return Task.FromResult("id"); });
        var sender = new FcmAlertSender(client, NullLogger<FcmAlertSender>.Instance);

        await sender.SendAlertAsync("fcm-token", "fcm", SampleContent(), CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Null(captured!.Notification); // data-only — no `notification` block (design §HIGH-2)
#pragma warning disable CS0618 // Token is Obsolete("Use Fid instead") — see FcmAlertSender's doc comment.
        Assert.Equal("fcm-token", captured.Token);
#pragma warning restore CS0618
        Assert.Equal("New access request", captured.Data!["title"]);
        Assert.Equal("Agent \"cursor\" requests access to \"filesystem\"", captured.Data!["body"]);
        Assert.Equal("access_request", captured.Data!["type"]);
        Assert.Equal("req-123", captured.Data!["accessRequestId"]);
        Assert.Equal("req-123", captured.Android!.CollapseKey);
        Assert.Equal(Priority.High, captured.Android!.Priority);
    }
}
