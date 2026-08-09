import { createFileRoute } from '@tanstack/react-router'
import { useState } from 'react'
import { api } from '@/lib/api'

type CliAuthorizeSearch = { code?: string }

export const Route = createFileRoute('/cli/authorize')({
  validateSearch: (search: Record<string, unknown>): CliAuthorizeSearch => ({
    code: typeof search.code === 'string' ? search.code.trim().toUpperCase() : undefined,
  }),
  component: CliAuthorizePage,
})

/** Exported for test harness — allows mounting CliAuthorizePage in a minimal router. */
export { CliAuthorizePage }

type PageState = 'idle' | 'approving' | 'denying' | 'approved' | 'denied' | 'error'

function CliAuthorizePage() {
  const { code: codeFromUrl } = Route.useSearch()
  // codeFromUrl — from ?code= query param (pre-filled, shown statically).
  // userCode — what the user typed manually, or falls back to the URL code on error
  // so the user can correct a garbled link without having to strip the query string.
  const [userCode, setUserCode] = useState('')
  const [state, setState] = useState<PageState>('idle')
  const [errorMsg, setErrorMsg] = useState<string | null>(null)
  // When the URL code returned 404 we fall back to the editable input so the user
  // can correct a truncated/garbled link.
  const [urlCodeFailed, setUrlCodeFailed] = useState(false)

  // Resolved code: prefer the URL param (unless it already failed), fall back to manual input.
  const effectiveCode = (codeFromUrl && !urlCodeFailed) ? codeFromUrl : userCode.trim().toUpperCase()

  async function handleApprove() {
    const trimmed = effectiveCode
    if (!trimmed) return
    setState('approving')
    setErrorMsg(null)
    try {
      await api.cli.approve(trimmed)
      setState('approved')
    } catch (err: unknown) {
      const status = err instanceof Error && 'status' in err ? (err as { status: number }).status : 0
      if (status === 401) {
        setErrorMsg('Please sign in to your Korat account first, then reopen this link.')
      } else if (status === 404) {
        // 404 from a URL-supplied code could be a garbled/truncated link — reveal the
        // editable input pre-filled with the attempted code so the user can correct it.
        if (codeFromUrl && !urlCodeFailed) {
          setUrlCodeFailed(true)
          setUserCode(codeFromUrl)
        }
        setErrorMsg(
          'This code is no longer valid — it may have expired, already been used, or been mistyped. ' +
          'Correct the code below or return to your terminal and run korat login again.'
        )
      } else {
        setErrorMsg('Something went wrong. Please try again.')
      }
      setState('error')
    }
  }

  async function handleDeny() {
    const trimmed = effectiveCode
    if (!trimmed) return
    setState('denying')
    setErrorMsg(null)
    try {
      await api.cli.deny(trimmed)
      setState('denied')
    } catch (err: unknown) {
      const status = err instanceof Error && 'status' in err ? (err as { status: number }).status : 0
      if (status === 401) {
        setErrorMsg('Please sign in to your Korat account first, then reopen this link.')
      } else if (status === 404) {
        if (codeFromUrl && !urlCodeFailed) {
          setUrlCodeFailed(true)
          setUserCode(codeFromUrl)
        }
        setErrorMsg(
          'This code is no longer valid — it may have expired, already been used, or been mistyped. ' +
          'Correct the code below or return to your terminal and run korat login again.'
        )
      } else {
        setErrorMsg('Something went wrong. Please try again.')
      }
      setState('error')
    }
  }

  if (state === 'approved') {
    return (
      <div className="flex min-h-screen items-center justify-center p-6">
        <div className="w-full max-w-md text-center space-y-4">
          <h1 className="text-2xl font-semibold">CLI access authorized</h1>
          <p className="text-muted-foreground text-sm">
            You have approved the CLI request. You can close this tab and return to
            your terminal.
          </p>
        </div>
      </div>
    )
  }

  if (state === 'denied') {
    return (
      <div className="flex min-h-screen items-center justify-center p-6">
        <div className="w-full max-w-md text-center space-y-4">
          <h1 className="text-2xl font-semibold">CLI access denied</h1>
          <p className="text-muted-foreground text-sm">
            You have denied the CLI request. You can close this tab.
          </p>
        </div>
      </div>
    )
  }

  const busy = state === 'approving' || state === 'denying'

  return (
    <div className="flex min-h-screen items-center justify-center p-6">
      <div className="w-full max-w-md space-y-8">
        <div className="space-y-2 text-center">
          <h1 className="text-2xl font-bold tracking-tight">Authorize CLI access</h1>
          <p className="text-muted-foreground text-sm">
            A Korat CLI process is requesting access to your account.
          </p>
        </div>

        <div className="rounded-lg border border-border bg-muted/40 p-6 space-y-4">
          {codeFromUrl && !urlCodeFailed ? (
            <>
              <p className="text-sm text-muted-foreground text-center">
                Confirm the code shown in your terminal:
              </p>
              <p className="text-center font-mono text-3xl font-bold tracking-widest" aria-label="User code">
                {codeFromUrl}
              </p>
            </>
          ) : (
            <div className="space-y-1.5">
              <label htmlFor="user-code" className="block text-sm font-medium">
                {urlCodeFailed ? 'Correct the code shown in your terminal' : 'Enter the code shown in your terminal'}
              </label>
              <input
                id="user-code"
                type="text"
                value={userCode}
                onChange={(e) => setUserCode(e.target.value.toUpperCase())}
                placeholder="XXXXXXXX"
                className="w-full rounded-md border border-input bg-background px-3 py-2 text-sm font-mono uppercase tracking-widest placeholder:normal-case placeholder:tracking-normal focus:outline-none focus:ring-2 focus:ring-ring"
                aria-label="Enter the code shown in your terminal"
              />
            </div>
          )}
        </div>

        {errorMsg && (
          <p className="text-xs text-destructive text-center" role="alert">
            {errorMsg}
          </p>
        )}

        <div className="flex flex-col gap-3">
          <button
            type="button"
            disabled={busy || !effectiveCode}
            onClick={handleApprove}
            className="w-full rounded-md bg-primary px-4 py-2.5 text-sm font-medium text-primary-foreground hover:bg-primary/90 disabled:opacity-50 transition-colors"
          >
            {state === 'approving' ? 'Approving...' : 'Approve'}
          </button>
          <button
            type="button"
            disabled={busy || !effectiveCode}
            onClick={handleDeny}
            className="w-full rounded-md border border-input bg-background px-4 py-2.5 text-sm font-medium hover:bg-muted disabled:opacity-50 transition-colors"
          >
            {state === 'denying' ? 'Denying...' : 'Deny'}
          </button>
        </div>

        <p className="text-center text-xs text-muted-foreground">
          Only approve if you initiated a <code className="font-mono">korat login</code> command.
          This authorizes the CLI to access your Korat account.
        </p>
      </div>
    </div>
  )
}
