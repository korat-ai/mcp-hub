using Korat.Cli.Commands;

namespace Korat.Cloud.ContractTests;

/// <summary>
/// C3 — 5-minute timeout in `korat connect`.
/// Tests the KORAT_CONNECT_APPROVAL_TIMEOUT_SECONDS injection seam and verifies
/// GetApprovalTimeout() correctly honours the override.
///
/// A full end-to-end timeout test (driving WaitForApprovalAsync against a
/// never-approved access request and asserting non-zero exit code + timeout message)
/// is deferred to tests/FOLLOWUPS.md because WaitForApprovalAsync instantiates
/// HttpClient internally and has no injection point for a test-server client.
/// See FOLLOWUPS.md entry: "C3-full-integration: inject HttpClient into WaitForApprovalAsync".
/// </summary>
[Collection("EnvironmentVariables")]
public sealed class ConnectTimeoutContractTests
{
    [Fact]
    public void GetApprovalTimeout_WithEnvOverride_ReturnsConfiguredDuration()
    {
        // C3: KORAT_CONNECT_APPROVAL_TIMEOUT_SECONDS must drive the CancelAfter deadline.
        var prior = Environment.GetEnvironmentVariable("KORAT_CONNECT_APPROVAL_TIMEOUT_SECONDS");
        try
        {
            Environment.SetEnvironmentVariable("KORAT_CONNECT_APPROVAL_TIMEOUT_SECONDS", "1");
            var timeout = ConnectCommand.GetApprovalTimeout();
            Assert.Equal(TimeSpan.FromSeconds(1), timeout);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KORAT_CONNECT_APPROVAL_TIMEOUT_SECONDS", prior);
        }
    }

    [Fact]
    public void GetApprovalTimeout_WithInvalidEnvValue_FallsBackToFiveMinutes()
    {
        var prior = Environment.GetEnvironmentVariable("KORAT_CONNECT_APPROVAL_TIMEOUT_SECONDS");
        try
        {
            Environment.SetEnvironmentVariable("KORAT_CONNECT_APPROVAL_TIMEOUT_SECONDS", "not-a-number");
            var timeout = ConnectCommand.GetApprovalTimeout();
            Assert.Equal(TimeSpan.FromMinutes(5), timeout);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KORAT_CONNECT_APPROVAL_TIMEOUT_SECONDS", prior);
        }
    }

    [Fact]
    public void GetApprovalTimeout_WithZeroValue_FallsBackToFiveMinutes()
    {
        // Zero is rejected (must be > 0).
        var prior = Environment.GetEnvironmentVariable("KORAT_CONNECT_APPROVAL_TIMEOUT_SECONDS");
        try
        {
            Environment.SetEnvironmentVariable("KORAT_CONNECT_APPROVAL_TIMEOUT_SECONDS", "0");
            var timeout = ConnectCommand.GetApprovalTimeout();
            Assert.Equal(TimeSpan.FromMinutes(5), timeout);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KORAT_CONNECT_APPROVAL_TIMEOUT_SECONDS", prior);
        }
    }
}
