const COMMIT = (import.meta.env.VITE_COMMIT_SHA as string | undefined) ?? 'dev';

interface Props {
  error?: Error;
}

export function AppCrashScreen({ error }: Props) {
  const isDev = import.meta.env.DEV;
  return (
    <div className="min-h-screen flex items-center justify-center p-8 bg-background text-foreground">
      <div className="max-w-md text-center space-y-4">
        <h1 className="text-xl font-semibold">Something broke.</h1>
        <p className="text-sm text-muted-foreground">
          Reload the page. If it keeps happening, share the details below.
        </p>
        {isDev && error && (
          <pre className="text-left text-xs font-mono bg-muted p-3 rounded overflow-auto max-h-64">
            {error.stack ?? error.message}
          </pre>
        )}
        <p className="text-xs font-mono text-muted-foreground">build {COMMIT}</p>
        <button
          className="bg-primary text-primary-foreground rounded-md px-4 py-2 text-sm hover:opacity-90"
          onClick={() => window.location.reload()}
        >
          Reload
        </button>
      </div>
    </div>
  );
}
