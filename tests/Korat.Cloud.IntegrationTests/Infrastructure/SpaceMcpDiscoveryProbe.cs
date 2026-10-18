using System.Collections.Concurrent;
using Korat.Domain;
using Korat.GrainInterfaces;
using Orleans;
using Orleans.Runtime;

namespace Korat.Cloud.IntegrationTests;

/// <summary>Proves routed requests do not depend on a full Space catalog scan.</summary>
public static class SpaceMcpDiscoveryProbe
{
    private static readonly ConcurrentDictionary<string, Scope> Scopes = new();

    public static Scope Block(string spaceId)
    {
        var scope = new Scope(spaceId);
        if (!Scopes.TryAdd(spaceId, scope))
            throw new InvalidOperationException("A discovery probe is already active for this Space.");
        return scope;
    }

    public sealed class Scope(string spaceId) : IDisposable
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        internal void RecordCall() => Interlocked.Increment(ref _calls);
        public void Dispose() => Scopes.TryRemove(new KeyValuePair<string, Scope>(spaceId, this));
    }

    public sealed class Filter : IIncomingGrainCallFilter
    {
        public async Task Invoke(IIncomingGrainCallContext context)
        {
            if (context.InterfaceMethod?.DeclaringType == typeof(ISpaceGrain)
                && context.InterfaceMethod.Name == nameof(ISpaceGrain.ListMcpServersAsync)
                && context.Grain is IAddressable addressable
                && Scopes.TryGetValue(addressable.GetPrimaryKeyString(), out var scope))
            {
                scope.RecordCall();
                throw new KoratDomainException(KoratErrorCode.DataStoreUnavailable,
                    "[TEST FAULT INJECTION] full Space discovery is unavailable.");
            }
            await context.Invoke();
        }
    }
}
