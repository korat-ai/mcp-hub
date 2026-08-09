using System.CommandLine;
using Korat.Cli.Util;

namespace Korat.Cli.Commands;

public static class VersionCommand
{
    public static Command Create()
    {
        var command = new Command("version", "Print the CLI version, install method, and upgrade command.");
        command.SetHandler(() =>
        {
            var info = CliVersion.Informational();
            Console.WriteLine($"korat {info}");

            // #101: report the install method and the correct upgrade path up front.
            var (installDescription, upgradeCommand) = UpgradeCommand.GetInstallInfo();
            Console.WriteLine($"Install method: {installDescription}");
            Console.WriteLine($"To upgrade:     {upgradeCommand}");
        });
        return command;
    }
}
