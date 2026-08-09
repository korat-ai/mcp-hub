using Korat.Cloud.Push;

namespace Korat.Cloud.IntegrationTests.Push;

public sealed class RoutingAlertPushSenderTests
{
    private sealed class RecordingSender(AlertSendResult result) : IAlertPushSender
    {
        public List<string> CalledPlatforms { get; } = new();
        public Task<AlertSendResult> SendAlertAsync(string token, string platform, AlertContent content, CancellationToken ct)
        {
            CalledPlatforms.Add(platform);
            return Task.FromResult(result);
        }
    }

    private static AlertContent SampleContent() => new("t", "b", new Dictionary<string, string>());

    [Theory]
    [InlineData("apns")]
    [InlineData("apns_sandbox")]
    public async Task Routes_Apns_Platforms_To_Apns_Sender(string platform)
    {
        var apns = new RecordingSender(AlertSendResult.Delivered);
        var fcm = new RecordingSender(AlertSendResult.Delivered);
        var router = new RoutingAlertPushSender(apns, fcm);

        await router.SendAlertAsync("tok", platform, SampleContent(), CancellationToken.None);

        Assert.Single(apns.CalledPlatforms);
        Assert.Empty(fcm.CalledPlatforms);
    }

    [Fact]
    public async Task Routes_Fcm_Platform_To_Fcm_Sender()
    {
        var apns = new RecordingSender(AlertSendResult.Delivered);
        var fcm = new RecordingSender(AlertSendResult.Delivered);
        var router = new RoutingAlertPushSender(apns, fcm);

        await router.SendAlertAsync("tok", "fcm", SampleContent(), CancellationToken.None);

        Assert.Empty(apns.CalledPlatforms);
        Assert.Single(fcm.CalledPlatforms);
    }

    [Fact]
    public async Task Unknown_Platform_Returns_TransientFailure_Without_Calling_Either_Sender()
    {
        var apns = new RecordingSender(AlertSendResult.Delivered);
        var fcm = new RecordingSender(AlertSendResult.Delivered);
        var router = new RoutingAlertPushSender(apns, fcm);

        var result = await router.SendAlertAsync("tok", "unknown_platform", SampleContent(), CancellationToken.None);

        Assert.Equal(AlertSendResult.TransientFailure, result);
        Assert.Empty(apns.CalledPlatforms);
        Assert.Empty(fcm.CalledPlatforms);
    }

    [Fact]
    public async Task NullAlertPushSender_Returns_TransientFailure()
    {
        var sender = new NullAlertPushSender();
        var result = await sender.SendAlertAsync("tok", "apns", SampleContent(), CancellationToken.None);
        Assert.Equal(AlertSendResult.TransientFailure, result);
    }
}
