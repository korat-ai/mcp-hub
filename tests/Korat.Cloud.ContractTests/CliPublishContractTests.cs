using Korat.Cli.Commands;

namespace Korat.Cloud.ContractTests;

[Collection("EnvironmentVariables")]
public sealed class CliPublishContractTests
{
    [Fact]
    public void LoginCommand_HasExpectedName()
    {
        var command = LoginCommand.Create();
        Assert.Equal("login", command.Name);
    }

    [Fact]
    public void UpCommand_HasExpectedName()
    {
        var command = UpCommand.Create();
        Assert.Equal("up", command.Name);
    }

    [Fact]
    public void McpAddCommand_HasNameAndCommandArguments()
    {
        var command = McpAddCommand.Create();
        Assert.Equal("add", command.Name);
        // 006-cli-stdio-bridge: the launch command moved from a positional
        // argument to a required --command option so we can keep the launch
        // string verbatim through shell parsing.
        Assert.Contains(command.Arguments, a => a.Name == "name");
        Assert.Contains(command.Options, o => o.Name == "command");
    }

    [Fact]
    public void LocalIdentityStore_CreatesPersistentNodeId()
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

            var store = new LocalIdentityStore();
            var identity = store.LoadOrCreate();
            Assert.False(string.IsNullOrWhiteSpace(identity.NodeId));
            Assert.True(File.Exists(path));
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
}
