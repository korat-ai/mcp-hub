namespace Korat.Domain;

/// <summary>
/// Increment 1 (HTTP MCP direct-to-Space): relocated from Korat.Cloud.Web.Inference (where it
/// originally lived alongside SsrfGuardedHttpClientFactory) so a grain hosted in Korat.Cloud
/// (HttpMcpProxyGrain) — and, in principle, a future Korat.Grains-hosted consumer — can consume
/// it without an illegal reverse project reference (Korat.Grains never depends on Korat.Cloud;
/// only Korat.Cloud → Korat.Grains is a valid direction). Mirrors the identical relocation
/// already done for IEnvelopeCrypto (see Korat.Domain.Persistence.IEnvelopeCrypto / Program.cs
/// comment "so Korat.Grains (ThreadGrain) can consume it too, without depending on this Cloud
/// host app").
/// </summary>
public interface IOutboundHttpClientFactory
{
    HttpClient CreateClient(string purposeLabel);
}
