using Korat.Cli.Commands;
using Korat.Cli.Util;

namespace Korat.Cloud.ContractTests;

[Collection("EnvironmentVariables")]
public sealed class CliConfigContractTests
{
    [Fact]
    public void LocalIdentityStore_DefaultCloudUrl_Is5191()
    {
        var home = Path.Combine(Path.GetTempPath(), $"korat-home-{Guid.NewGuid():N}");
        var path = Path.Combine(home, "config.json");
        Directory.CreateDirectory(home);

        var priorConfig = Environment.GetEnvironmentVariable("KORAT_CONFIG");
        var priorHome = Environment.GetEnvironmentVariable("HOME");
        var priorUserProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        try
        {
            Environment.SetEnvironmentVariable("KORAT_CONFIG", path);
            Environment.SetEnvironmentVariable("HOME", home);
            Environment.SetEnvironmentVariable("USERPROFILE", home);

            var identity = new LocalIdentityStore().LoadOrCreate();
            Assert.Equal("http://localhost:5191", identity.CloudUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KORAT_CONFIG", priorConfig);
            Environment.SetEnvironmentVariable("HOME", priorHome);
            Environment.SetEnvironmentVariable("USERPROFILE", priorUserProfile);
            if (Directory.Exists(home))
                Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void ConnectCommand_ValidateIdentity_FailsWhenNodeIdMissing()
    {
        // TryValidateIdentity lives on LocalIdentityStore (not ConnectCommand — SP4 refactor
        // moved per-field validation to the store so ConnectCommand can stay focused on flow).
        var identity = new LocalIdentity { CloudUrl = "http://localhost:5191", NodeId = "" };
        Assert.False(LocalIdentityStore.TryValidateIdentity(identity, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void ConnectCommand_DefaultApprovalTimeout_IsFiveMinutes()
    {
        var prior = Environment.GetEnvironmentVariable("KORAT_CONNECT_APPROVAL_TIMEOUT_SECONDS");
        try
        {
            Environment.SetEnvironmentVariable("KORAT_CONNECT_APPROVAL_TIMEOUT_SECONDS", null);
            Assert.Equal(TimeSpan.FromMinutes(5), ConnectCommand.GetApprovalTimeout());
        }
        finally
        {
            Environment.SetEnvironmentVariable("KORAT_CONNECT_APPROVAL_TIMEOUT_SECONDS", prior);
        }
    }

    [Fact]
    public void BrowserLauncher_BuildApproveUrl_IncludesRequestId()
    {
        // The approve link targets the SPA route `/approve/$requestId` under the /app
        // basepath (requestId is a path segment). The old `/space/approve.html?requestId=`
        // form matched no route and the browser downloaded the page instead of rendering it.
        // SP4: no ownerToken — the browser's session cookie authenticates the approval page.
        var url = BrowserLauncher.BuildApproveUrl(
            "http://localhost:5191",
            "req-123");

        Assert.Contains("/app/approve/req-123", url);
        Assert.DoesNotContain("approve.html", url);
        Assert.DoesNotContain("ownerToken", url);
    }

    [Fact]
    public void ConnectCommand_HasExpectedName()
    {
        var command = ConnectCommand.Create();
        Assert.Equal("connect", command.Name);
    }
}
