using System.Reflection;

namespace Korat.Cli.Util;

public static class CliVersion
{
    /// <summary>Full informational version e.g. "0.2.8+abc123".</summary>
    public static string Informational() =>
        typeof(CliVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.0.0-dev+unknown";

    /// <summary>Bare SemVer for comparison/wire e.g. "0.2.8" from "0.2.8+abc123".</summary>
    public static string Bare() => Informational().Split('+')[0].TrimStart('v', 'V');
}
