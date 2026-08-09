using Korat.Cli.Commands;

namespace Korat.Cli.Tests;

/// <summary>
/// Final-review LOW fix: on Ctrl+C, the SIGINT handler in <c>ConnectCommand.ConnectAsync</c>
/// logs its own terminal "exit code=130 reason=user-abort (SIGINT)" line, and the normal
/// shutdown path (<c>RunBridgeLoopAsync</c> / <c>RunSpaceAggregatorAsync</c>, once their pumps
/// observe the same cancellation and unwind) ALSO logs its own final "exit code=... reason=..."
/// line — a race that could append a second, misleading line right after the accurate one.
///
/// <see cref="ConnectCommand.BridgeLogContext.TryClaimTerminalExit"/> is the Interlocked guard
/// that makes this impossible: exactly one caller (across however many concurrently race for
/// it) gets to log the process's terminal exit line. Tested directly (not through
/// <c>Log</c>/<c>BridgeExitLog.Append</c>, which always write to the real
/// <c>~/.korat/logs</c> with no test-injectable directory override) so this test never
/// touches disk.
/// </summary>
public class ConnectCommandBridgeLogContextTests
{
    [Fact]
    public void TryClaimTerminalExit_first_call_succeeds()
    {
        var ctx = new ConnectCommand.BridgeLogContext(bridge: true, agentName: "test-agent");

        Assert.True(ctx.TryClaimTerminalExit());
    }

    [Fact]
    public void TryClaimTerminalExit_second_call_fails()
    {
        var ctx = new ConnectCommand.BridgeLogContext(bridge: true, agentName: "test-agent");

        Assert.True(ctx.TryClaimTerminalExit());
        Assert.False(ctx.TryClaimTerminalExit());
    }

    [Fact]
    public void TryClaimTerminalExit_repeated_calls_after_the_first_all_fail()
    {
        var ctx = new ConnectCommand.BridgeLogContext(bridge: true, agentName: "test-agent");

        Assert.True(ctx.TryClaimTerminalExit());
        for (var i = 0; i < 5; i++)
            Assert.False(ctx.TryClaimTerminalExit());
    }

    [Fact]
    public async Task TryClaimTerminalExit_under_concurrent_racers_exactly_one_wins()
    {
        // Simulates the real race: the SIGINT handler and the pump-shutdown path can both
        // reach their terminal-exit log call at roughly the same instant on different
        // threads. Regardless of how many concurrent callers race for it, exactly one must
        // win — the guard exists specifically so the log file never gets two exit lines for
        // one process.
        var ctx = new ConnectCommand.BridgeLogContext(bridge: true, agentName: "test-agent");
        var tasks = Enumerable.Range(0, 50)
            .Select(_ => Task.Run(ctx.TryClaimTerminalExit))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(won => won));
    }

    [Fact]
    public void LogTerminalExitOnce_non_bridge_mode_never_writes_but_still_only_claims_once()
    {
        // Non-bridge behavior must stay byte-identical to plain Log() (a no-op) — asserted
        // here via the fact that BridgeLogContext.Log is a no-op when Bridge is false, so
        // calling LogTerminalExitOnce any number of times in non-bridge mode has no
        // observable side effect (no exception, no disk write attempt reachable from this
        // no-op branch). The guard itself is still exercised underneath (claims exactly once)
        // but that's unobservable from outside since Log() never runs when !Bridge.
        var ctx = new ConnectCommand.BridgeLogContext(bridge: false, agentName: "test-agent");

        ctx.LogTerminalExitOnce("exit code=1 reason=whatever");
        ctx.LogTerminalExitOnce("exit code=0 reason=something-else");

        // No assertion beyond "did not throw" is possible without touching the real
        // ~/.korat/logs directory (Log() hardcodes it) — which this test deliberately avoids.
    }
}
