using Korat.Mcp;
namespace Korat.Cli.Mcp.Aggregation;

public sealed record SpaceDiff(
    IReadOnlyList<ServerDescriptor> GrantedAdded,
    IReadOnlyList<ServerDescriptor> GrantedRemoved,
    IReadOnlyList<ServerDescriptor> UngrantedAdded,
    IReadOnlyList<ServerDescriptor> UngrantedRemoved)
{
    public bool HasChanges =>
        GrantedAdded.Count > 0 || GrantedRemoved.Count > 0 ||
        UngrantedAdded.Count > 0 || UngrantedRemoved.Count > 0;
}

internal sealed class SpaceWatcher
{
    private readonly Func<CancellationToken, Task<SpaceSnapshot>> _discover;
    private readonly BackendSessionManager _sessions;
    private readonly AggregateCatalog _catalog;
    private readonly Func<CancellationToken, Task> _onChanged;
    private readonly TimeSpan _interval;
    private SpaceSnapshot _previous;

    // serverId -> the slug it is ACTUALLY registered under in _sessions/_catalog (which may
    // carry a UniqueSlug collision suffix). Kept in lockstep with _previous.Granted: an id is
    // present here iff it's in _previous.Granted. This is the source of truth for "already
    // taken" slugs on the next tick — see ReconcileAsync's GrantedAdded handling.
    private readonly Dictionary<string, string> _slugsByServerId;

    public SpaceWatcher(
        Func<CancellationToken, Task<SpaceSnapshot>> discover,
        BackendSessionManager sessions,
        AggregateCatalog catalog,
        Func<CancellationToken, Task> onChanged,
        SpaceSnapshot baseline,
        IReadOnlyDictionary<string, string>? baselineSlugsByServerId = null,
        TimeSpan? interval = null)
    {
        _discover = discover;
        _sessions = sessions;
        _catalog = catalog;
        _onChanged = onChanged;
        _previous = baseline;
        _slugsByServerId = baselineSlugsByServerId is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(baselineSlugsByServerId);
        _interval = interval ?? ResolveDefaultInterval();
    }

    private static TimeSpan ResolveDefaultInterval()
    {
        var raw = Environment.GetEnvironmentVariable("KORAT_SPACE_POLL_SECONDS");
        if (int.TryParse(raw, out var seconds) && seconds >= 1)
            return TimeSpan.FromSeconds(seconds);
        // Clamp to >=1s; fallback is 8s.
        return TimeSpan.FromSeconds(8);
    }

    /// <summary>
    /// Computes the set-difference between two snapshots. Comparisons are by server Id.
    /// Slug assignment (and disambiguation of two servers whose display names collapse to the
    /// same slug) happens separately in <see cref="ReconcileAsync"/> / <c>ConnectCommand</c>
    /// via <see cref="ToolNamespacer.UniqueSlug"/> — this method only diffs server identity.
    /// </summary>
    public static SpaceDiff ComputeDiff(SpaceSnapshot prev, SpaceSnapshot cur)
    {
        var prevGrantedIds = prev.Granted.ToDictionary(s => s.Id);
        var curGrantedIds  = cur.Granted.ToDictionary(s => s.Id);

        var prevUngrantedIds = prev.Ungranted.ToDictionary(s => s.Id);
        var curUngrantedIds  = cur.Ungranted.ToDictionary(s => s.Id);

        var grantedAdded   = cur.Granted.Where(s => !prevGrantedIds.ContainsKey(s.Id)).ToList();
        var grantedRemoved = prev.Granted.Where(s => !curGrantedIds.ContainsKey(s.Id)).ToList();
        var ungrantedAdded   = cur.Ungranted.Where(s => !prevUngrantedIds.ContainsKey(s.Id)).ToList();
        var ungrantedRemoved = prev.Ungranted.Where(s => !curUngrantedIds.ContainsKey(s.Id)).ToList();

        return new SpaceDiff(grantedAdded, grantedRemoved, ungrantedAdded, ungrantedRemoved);
    }

    /// <summary>
    /// Applies one snapshot to the catalog and sessions. Returns true if anything changed.
    /// </summary>
    /// <remarks>
    /// Retry invariant: only servers that are ACTUALLY open are committed into <c>_previous</c>.
    /// A server whose open fails (publisher offline, denied, etc.) is excluded from <c>_previous</c>,
    /// so <see cref="ComputeDiff"/> still sees it in <c>GrantedAdded</c> next tick and retries it.
    /// <c>onChanged</c> is fired only when the catalog actually changed, so a perpetually-offline
    /// server does not spam <c>list_changed</c> every tick.
    /// </remarks>
    public async Task<bool> ReconcileAsync(SpaceSnapshot cur, CancellationToken ct)
    {
        var diff = ComputeDiff(_previous, cur);

        // Granted servers that are actually open after this tick (retained + newly opened).
        var openGranted = new List<ServerDescriptor>();
        var catalogChanged = false;

        // Retained: already-open granted servers that are still granted — carry them forward.
        foreach (var s in cur.Granted)
            if (_previous.Granted.Any(p => p.Id == s.Id))
                openGranted.Add(s);

        // Newly granted: try to open. A failed open is skipped and NOT recorded in openGranted,
        // so it reappears in GrantedAdded next tick and retries automatically.
        //
        // Slug disambiguation: seed `taken` from the slugs already assigned to currently-open
        // granted servers (_slugsByServerId) — NOT a fresh empty set — so a server added this
        // tick whose display name collapses to the same slug as an already-open one gets a
        // distinct suffixed slug via UniqueSlug, rather than silently sharing/clobbering the
        // existing _sessionsBySlug / catalog entry (which would mis-route tools/call). The slug
        // is computed exactly once per server and reused for both OpenAsync (session routing)
        // and SetGranted (catalog registration) — see BackendSession.Slug, which is the single
        // source of truth both call sites end up keyed on.
        var taken = new HashSet<string>(_slugsByServerId.Values);
        foreach (var s in diff.GrantedAdded)
        {
            try
            {
                var slug = ToolNamespacer.UniqueSlug(s.DisplayName, s.Id, taken);
                var tools = await _sessions.OpenAsync(s, slug, ct);
                _catalog.SetGranted(s.Id, slug, s.DisplayName, tools);
                _slugsByServerId[s.Id] = slug;
                openGranted.Add(s);
                catalogChanged = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* publisher offline / open failed — skip; retried next tick */ }
        }

        // Revoked / removed granted: close + drop from catalog.
        foreach (var s in diff.GrantedRemoved)
        {
            try { await _sessions.CloseAsync(s.Id); } catch { /* best-effort */ }
            _catalog.RemoveGranted(s.Id);
            _slugsByServerId.Remove(s.Id);
            catalogChanged = true;
        }

        // Ungranted set changed: refresh request-access tools.
        if (diff.UngrantedAdded.Count > 0 || diff.UngrantedRemoved.Count > 0)
        {
            _catalog.SetUngranted(cur.Ungranted);
            catalogChanged = true;
        }

        // Commit only actually-open granted servers; failed opens stay out so they retry.
        _previous = new SpaceSnapshot(openGranted, cur.Ungranted);

        if (catalogChanged) await _onChanged(ct);
        return catalogChanged;
    }

    /// <summary>
    /// Polls the space on the configured interval, reconciling each discovered snapshot.
    /// Intended to run as a long-lived background task; cancelled via <paramref name="ct"/>.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, ct);
                var cur = await _discover(ct);
                await ReconcileAsync(cur, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch
            {
                // A transient discovery/reconcile failure must not kill the watcher; next tick retries.
            }
        }
    }
}
