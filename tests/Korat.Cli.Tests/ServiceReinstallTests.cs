using Korat.Cli.Commands;
using Korat.Cli.Service;

namespace Korat.Cli.Tests;

/// <summary>
/// Unit tests for <c>korat service reinstall</c> (Bug 4).
///
/// Verifies that reinstall = uninstall (ignore-absent) then install,
/// using a stub <see cref="IServiceController"/> so no OS interaction occurs.
/// </summary>
public class ServiceReinstallTests
{
    // ──────────────────────────────────────────────────────────────────────────
    // Stub controller
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class StubController : IServiceController
    {
        public List<string> Calls { get; } = [];
        public bool ThrowOnUninstall { get; init; }
        public bool ThrowOnInstall { get; init; }

        public Task InstallAsync(CancellationToken ct = default)
        {
            if (ThrowOnInstall) throw new InvalidOperationException("install failed");
            Calls.Add("install");
            return Task.CompletedTask;
        }

        public Task UninstallAsync(CancellationToken ct = default)
        {
            if (ThrowOnUninstall) throw new InvalidOperationException("not installed");
            Calls.Add("uninstall");
            return Task.CompletedTask;
        }

        public Task<ServiceStatus> GetStatusAsync(CancellationToken ct = default)
            => Task.FromResult(new ServiceStatus(false, false, null));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Tests
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reinstall_calls_uninstall_then_install_in_order()
    {
        var ctrl = new StubController();
        await ServiceCommand.ReinstallWithControllerAsync(ctrl);

        Assert.Equal(["uninstall", "install"], ctrl.Calls);
    }

    [Fact]
    public async Task Reinstall_continues_to_install_when_uninstall_throws()
    {
        // Uninstall "not installed" should not abort the command.
        var ctrl = new StubController { ThrowOnUninstall = true };
        await ServiceCommand.ReinstallWithControllerAsync(ctrl);

        // install must still be called even though uninstall threw.
        Assert.Equal(["install"], ctrl.Calls);
    }

    [Fact]
    public async Task Reinstall_sets_ExitCode_1_when_install_throws()
    {
        var ctrl = new StubController { ThrowOnInstall = true };
        Environment.ExitCode = 0;

        await ServiceCommand.ReinstallWithControllerAsync(ctrl);

        Assert.Equal(1, Environment.ExitCode);
        // Clean up so other tests are not affected.
        Environment.ExitCode = 0;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Subcommand presence (ensure `reinstall` is registered in the command tree)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ServiceCommand_exposes_reinstall_subcommand()
    {
        var service = ServiceCommand.Create();
        var names = service.Subcommands.Select(c => c.Name).ToList();
        Assert.Contains("reinstall", names);
    }
}
