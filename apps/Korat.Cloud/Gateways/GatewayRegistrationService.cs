using Korat.Domain;
using Korat.GrainInterfaces;

namespace Korat.Cloud.Gateways;

/// <summary>
/// Registers and keeps-alive the stable gateway grain for this silo instance.
/// This satisfies G2: the gateway id is stable per silo so that sessions opened
/// during a connection resolve to a real, registered gateway rather than a
/// brand-new random GUID that has never been registered anywhere.
/// </summary>
public sealed class GatewayRegistrationService(
    IClusterClient clusterClient,
    IConfiguration configuration,
    ILogger<GatewayRegistrationService> logger) : BackgroundService
{
    private GatewayId StableGatewayId =>
        new(configuration["Korat:Cloud:GatewayId"] ?? Environment.MachineName);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var gatewayId = StableGatewayId;
        var grain = clusterClient.GetGrain<IGatewayGrain>(gatewayId.Value);

        try
        {
            await grain.RegisterAsync();
            logger.LogInformation("Gateway registered gatewayId={GatewayId}", gatewayId.Value);
        }
        catch (Exception ex)
        {
            logger.LogError("Gateway registration failed gatewayId={GatewayId} errorType={ErrorType}",
                gatewayId.Value, ex.GetType().Name);
            // Do not abort — the gateway can still route sessions; heartbeats will catch up.
        }

        // Keep-alive: heartbeat every 30 seconds while the silo is running.
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await grain.HeartbeatAsync();
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Gateway heartbeat failed gatewayId={GatewayId} errorType={ErrorType}",
                    gatewayId.Value, ex.GetType().Name);
            }
        }
    }
}
