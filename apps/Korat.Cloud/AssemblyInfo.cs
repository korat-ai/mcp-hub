using System.Runtime.CompilerServices;

// Allow Korat.Auth.Tests to access internal members for white-box unit testing
// (e.g. RateLimiterRegistration.ResolveClientIp, future internal helpers).
[assembly: InternalsVisibleTo("Korat.Auth.Tests")]
// Allow Korat.Cloud.IntegrationTests to call internal members (e.g. SessionReaperService.SweepAsync).
[assembly: InternalsVisibleTo("Korat.Cloud.IntegrationTests")]
// Increment 2 (HTTP MCP OAuth), Task 2: allow Korat.Cloud.ContractTests to call
// McpOAuthDiscoveryService.CanonicalUrlEquals directly (plan-specified internal, unit-tested).
[assembly: InternalsVisibleTo("Korat.Cloud.ContractTests")]
