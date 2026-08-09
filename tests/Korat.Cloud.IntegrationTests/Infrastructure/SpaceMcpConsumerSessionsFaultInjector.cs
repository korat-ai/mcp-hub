using System.Collections.Concurrent;
using Korat.Cloud.Mcp.Space;
using Korat.Domain;
using Orleans;
using Orleans.Runtime;

namespace Korat.Cloud.IntegrationTests;

/// <summary>
/// Space-MCP inc-2a, Task 7 (SF-1 fail-closed test support): mirrors
/// <see cref="AgentCreateFaultInjector"/>'s precedent — a silo-wide
/// <see cref="IIncomingGrainCallFilter"/> that throws for an ARMED
/// <see cref="ISpaceMcpConsumerSessionsGrain.RegisterAsync"/> call instead of running it,
/// reproducing "the registry registration failed" deterministically (the volatile in-memory
/// registry grain has no real failure mode of its own to exploit — e.g. no DB constraint to
/// violate — so this is the only realistic way to prove <c>SpaceMcpAggregatorGrain</c>'s
/// SF-1 fix: register-BEFORE-<c>_initialized=true</c> means a registration failure aborts
/// <c>InitializeCoreAsync</c> cleanly (never leaving <c>_initialized</c> true with an
/// unregistered live session), and a later retry re-attempts registration from scratch instead
/// of short-circuiting on the cached-result guard.
///
/// Armed by the TARGET GRAIN's own primary key (the consumer identity's <c>ConsumerId.Value</c>,
/// i.e. the registry grain being called) — not an argument value — because the test derives that
/// identity deterministically BEFORE triggering initialize (mirrors the plan's own Task 7 test),
/// so arming ahead of the call is always possible. No-op for every other call.
/// </summary>
public static class SpaceMcpConsumerSessionsFaultInjector
{
    private static readonly ConcurrentDictionary<string, bool> ArmedIdentities = new();

    /// <summary>Arms <paramref name="consumerIdentityValue"/>: the NEXT
    /// <see cref="ISpaceMcpConsumerSessionsGrain.RegisterAsync"/> call targeting the registry
    /// grain keyed by this identity throws instead of running.</summary>
    public static void Arm(string consumerIdentityValue) => ArmedIdentities[consumerIdentityValue] = true;

    /// <summary>Disarms <paramref name="consumerIdentityValue"/> — best-effort cleanup; safe to
    /// call even if never armed.</summary>
    public static void Disarm(string consumerIdentityValue) => ArmedIdentities.TryRemove(consumerIdentityValue, out _);

    private static bool IsArmed(string consumerIdentityValue) => ArmedIdentities.ContainsKey(consumerIdentityValue);

    public sealed class Filter : IIncomingGrainCallFilter
    {
        public async Task Invoke(IIncomingGrainCallContext context)
        {
            if (context.InterfaceMethod?.DeclaringType == typeof(ISpaceMcpConsumerSessionsGrain)
                && context.InterfaceMethod.Name == nameof(ISpaceMcpConsumerSessionsGrain.RegisterAsync)
                && context.Grain is IAddressable addressable
                && IsArmed(addressable.GetPrimaryKeyString()))
            {
                // KoratDomainException (not a bare framework exception) — Orleans-codec-serializable
                // (registered for the "Korat" namespace, see SiloConfigurator/ClientConfigurator),
                // so it round-trips cleanly across the grain-call boundary instead of risking a
                // masking CodecNotFoundException for an arbitrary BCL exception type. Mirrors
                // AgentCreateFaultInjector's own reasoning.
                throw new KoratDomainException(KoratErrorCode.DataStoreUnavailable,
                    "[TEST FAULT INJECTION] forced ISpaceMcpConsumerSessionsGrain.RegisterAsync failure.");
            }

            await context.Invoke();
        }
    }
}
