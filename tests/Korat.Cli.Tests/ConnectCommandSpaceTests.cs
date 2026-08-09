using Korat.Cli.Commands;
using Xunit;

namespace Korat.Cli.Tests;

/// <summary>
/// 028 T11: unit tests for the pure --space flag validator. The full aggregator
/// path needs live cloud/relay infra and is covered by T12 manual E2E.
/// </summary>
public class ConnectCommandSpaceTests
{
    [Fact]
    public void Space_requires_bridge() =>
        Assert.NotNull(ConnectCommand.ValidateSpaceFlags(space: true, serverName: null, bridge: false, send: null, waitResponse: false));

    [Fact]
    public void Space_rejects_server_name() =>
        Assert.NotNull(ConnectCommand.ValidateSpaceFlags(true, "github", true, null, false));

    [Fact]
    public void Space_rejects_send() =>
        Assert.NotNull(ConnectCommand.ValidateSpaceFlags(true, null, true, "hi", false));

    [Fact]
    public void Space_valid_with_bridge_and_no_server() =>
        Assert.Null(ConnectCommand.ValidateSpaceFlags(true, null, true, null, false));

    [Fact]
    public void Non_space_requires_server_name() =>
        Assert.NotNull(ConnectCommand.ValidateSpaceFlags(false, null, false, null, false));

    [Fact]
    public void Non_space_with_server_name_ok() =>
        Assert.Null(ConnectCommand.ValidateSpaceFlags(false, "github", false, null, false));
}
