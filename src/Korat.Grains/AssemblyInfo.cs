using System.Runtime.CompilerServices;

// Allow Korat.Cloud.IntegrationTests to call internal members for white-box unit testing
// (e.g. ChannelBindingGrain.IsBotIdUniqueViolation — fable #185 HIGH-1).
[assembly: InternalsVisibleTo("Korat.Cloud.IntegrationTests")]
