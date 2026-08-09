using Korat.Cli.Commands;

namespace Korat.Cli.Service;

/// <summary>
/// Watches <c>config.json</c> for changes and fires <see cref="Changed"/> with an
/// AUTHORITATIVELY-loaded <see cref="LocalIdentity"/> after a 500 ms debounce (editors often
/// generate multiple consecutive file-system events for a single save). If the file is missing,
/// empty, or unparseable at fire time (e.g. observed mid-atomic-save), the event is SUPPRESSED
/// (not fired with a minted-empty identity) so the live reconcile never mistakes a transient
/// read for "owner removed everything" — see B1 / <see cref="LocalIdentityStore.LoadAuthoritative"/>.
///
/// Uses <see cref="FileSystemWatcher"/> internally. Callers should dispose this instance
/// to release the OS watch handle.
/// </summary>
internal sealed class ConfigWatcher : IDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(500);

    private readonly string _configPath;
    private readonly LocalIdentityStore _store;
    private readonly FileSystemWatcher _watcher;
    private CancellationTokenSource? _debounceCts;
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>
    /// Raised (on a thread-pool thread) after each debounced config change, with the
    /// newly-loaded identity. Exceptions thrown by handlers are swallowed and logged.
    /// </summary>
    public event Action<LocalIdentity>? Changed;

    public ConfigWatcher(string configPath)
    {
        _configPath = configPath;
        _store = new LocalIdentityStore(configPath);

        var dir = Path.GetDirectoryName(configPath)
            ?? throw new ArgumentException("configPath must include a directory.", nameof(configPath));
        var file = Path.GetFileName(configPath);

        _watcher = new FileSystemWatcher(dir, file)
        {
            // FileName is REQUIRED so the atomic rename that LocalIdentityStore.Save performs
            // (write temp file, then File.Move(temp -> config.json, overwrite)) is observed:
            // on macOS/Linux that replace surfaces as a Renamed (and sometimes Created) event,
            // NOT a LastWrite/Size Changed event. Without handling Renamed the watcher would
            // miss every save and live reconcile would silently never fire.
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };

        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        // RenamedEventArgs derives from FileSystemEventArgs, so OnFileChanged handles it too.
        _watcher.Renamed += OnFileChanged;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        CancellationTokenSource newCts;
        CancellationTokenSource? oldCts;

        lock (_lock)
        {
            if (_disposed) return;
            oldCts = _debounceCts;
            newCts = new CancellationTokenSource();
            _debounceCts = newCts;
        }

        // Cancel the previous pending debounce (if any).
        try { oldCts?.Cancel(); oldCts?.Dispose(); } catch { /* best-effort */ }

        var ct = newCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DebounceDelay, ct);
            }
            catch (OperationCanceledException)
            {
                return; // superseded by a newer event
            }

            LocalIdentity identity;
            try
            {
                // B1: authoritative read — NEVER LoadOrCreate() here. On a parse error / empty
                // mid-atomic-write read, LoadOrCreate mints a FRESH identity with empty server +
                // inference-point sets, which the reconcile would interpret as "owner removed
                // everything" and hard-delete every cloud server AND inference point (+ revoke
                // keys). LoadAuthoritative throws instead; we suppress the reconcile and wait for
                // the next file event once the file is whole again.
                identity = _store.LoadAuthoritative();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[service] config reload skipped — config unreadable (transient/corrupt): {ex.Message}");
                return;
            }

            try
            {
                Changed?.Invoke(identity);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[service] config-changed handler failed: {ex.Message}");
            }
        }, ct);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _watcher.EnableRaisingEvents = false;
        _watcher.Changed -= OnFileChanged;
        _watcher.Created -= OnFileChanged;
        _watcher.Renamed -= OnFileChanged;
        _watcher.Dispose();

        CancellationTokenSource? cts;
        lock (_lock) { cts = _debounceCts; _debounceCts = null; }
        try { cts?.Cancel(); cts?.Dispose(); } catch { /* best-effort */ }
    }
}
