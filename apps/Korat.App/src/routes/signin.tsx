import { createFileRoute, Link, useSearch } from '@tanstack/react-router'
import { useState } from 'react'
import { useMagicLinkRequest } from '@/hooks/useMagicLinkRequest'

type SigninSearch = { returnUrl?: string; error?: string }

export const Route = createFileRoute('/signin')({
  // Return type declared explicitly with optional keys so TanStack Router
  // treats search params as optional at Link/navigate call sites
  // (`<Link to="/signin">` without search must typecheck).
  validateSearch: (search: Record<string, unknown>): SigninSearch => {
    const out: SigninSearch = {}
    if (typeof search.returnUrl === 'string') out.returnUrl = search.returnUrl
    if (typeof search.error === 'string') out.error = search.error
    return out
  },
  component: SignInPage,
})

/** Exported for test harness — allows mounting SignInPage in a minimal router. */
export { SignInPage }

function providerHref(provider: string, returnUrl?: string) {
  const params = new URLSearchParams()
  if (returnUrl) params.set('returnUrl', returnUrl)
  const qs = params.toString()
  return `/signin/${provider}${qs ? `?${qs}` : ''}`
}

const ERROR_MESSAGES: Record<string, string> = {
  unverified_email:
    "We couldn't find a verified email on your account. Your identity provider (GitHub/Google) only shares your primary email if it's verified — verify your primary email with the provider, then sign in again.",
  github: "GitHub sign-in didn't complete. Please try again.",
  google: "Google sign-in didn't complete. Please try again.",
}

function getErrorMessage(error: string | undefined): string | null {
  if (!error) return null
  return ERROR_MESSAGES[error] ?? "Sign-in didn't complete. Please try again."
}

function SignInPage() {
  const { returnUrl, error } = useSearch({ from: '/signin' })
  const [email, setEmail] = useState('')
  const [submitted, setSubmitted] = useState(false)

  const errorMessage = getErrorMessage(error)

  const magicLink = useMagicLinkRequest()

  function handleMagicLink(e: React.FormEvent) {
    e.preventDefault()
    // onSuccess (not onSettled): the anti-enumeration story applies to the
    // server's 2xx-for-unknown-email response, NOT to transport/server
    // failures. Show the generic copy only when the request actually reached
    // the server; surface a retry hint on transport failure instead.
    magicLink.mutate(
      { email },
      { onSuccess: () => setSubmitted(true) },
    )
  }

  if (submitted) {
    return (
      <div className="flex min-h-screen items-center justify-center p-6">
        <div className="w-full max-w-md space-y-5 text-center">
          <h1 className="text-2xl font-semibold">Check your email</h1>
          <p className="text-muted-foreground">
            If it's registered, a sign-in link is on its way to{' '}
            <span className="font-medium text-foreground break-all">{email}</span>.
          </p>
          <div className="rounded-md border border-border/60 bg-muted/40 px-4 py-3 text-left text-sm text-muted-foreground space-y-1.5">
            <p>• The link is valid for <span className="font-medium text-foreground">1 hour</span> and can be used once.</p>
            <p>• No email? <span className="font-medium text-foreground">Check your spam / junk folder</span> — the first one often lands there.</p>
            <p>• Wrong address? Go back and try again.</p>
          </div>
          <button
            type="button"
            onClick={() => setSubmitted(false)}
            className="text-sm underline underline-offset-2 text-muted-foreground hover:text-foreground"
          >
            ← Use a different email
          </button>
        </div>
      </div>
    )
  }


  return (
    <div className="flex min-h-screen items-center justify-center p-6">
      <div className="w-full max-w-md space-y-8">
        <div className="space-y-2 text-center">
          <h1 className="text-3xl font-bold tracking-tight">Sign in to Korat</h1>
          <p className="text-muted-foreground text-sm">
            Use a provider or a magic link below.
          </p>
        </div>

        {errorMessage && (
          <div
            role="alert"
            className="rounded-md border border-destructive/40 bg-destructive/10 px-4 py-3 text-sm text-destructive"
          >
            {errorMessage}
          </div>
        )}

        {/* Provider buttons */}
        <div className="space-y-3">
          <a
            href={providerHref('github', returnUrl)}
            className="flex w-full items-center justify-center gap-3 rounded-md border border-input bg-background px-4 py-2.5 text-sm font-medium shadow-sm hover:bg-muted transition-colors"
          >
            <svg viewBox="0 0 16 16" className="size-4 fill-current" aria-hidden="true">
              <path d="M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82a7.69 7.69 0 0 1 2-.27c.68 0 1.36.09 2 .27 1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.013 8.013 0 0 0 16 8c0-4.42-3.58-8-8-8z" />
            </svg>
            Continue with GitHub
          </a>

          <a
            href={providerHref('google', returnUrl)}
            className="flex w-full items-center justify-center gap-3 rounded-md border border-input bg-background px-4 py-2.5 text-sm font-medium shadow-sm hover:bg-muted transition-colors"
          >
            <svg viewBox="0 0 24 24" className="size-4" aria-hidden="true">
              <path
                fill="#4285F4"
                d="M22.56 12.25c0-.78-.07-1.53-.2-2.25H12v4.26h5.92c-.26 1.37-1.04 2.53-2.21 3.31v2.77h3.57c2.08-1.92 3.28-4.74 3.28-8.09z"
              />
              <path
                fill="#34A853"
                d="M12 23c2.97 0 5.46-.98 7.28-2.66l-3.57-2.77c-.98.66-2.23 1.06-3.71 1.06-2.86 0-5.29-1.93-6.16-4.53H2.18v2.84C3.99 20.53 7.7 23 12 23z"
              />
              <path
                fill="#FBBC05"
                d="M5.84 14.09c-.22-.66-.35-1.36-.35-2.09s.13-1.43.35-2.09V7.07H2.18C1.43 8.55 1 10.22 1 12s.43 3.45 1.18 4.93l3.66-2.84z"
              />
              <path
                fill="#EA4335"
                d="M12 5.38c1.62 0 3.06.56 4.21 1.64l3.15-3.15C17.45 2.09 14.97 1 12 1 7.7 1 3.99 3.47 2.18 7.07l3.66 2.84c.87-2.6 3.3-4.53 6.16-4.53z"
              />
            </svg>
            Continue with Google
          </a>
        </div>

        <div className="relative">
          <div className="absolute inset-0 flex items-center">
            <span className="w-full border-t border-border" />
          </div>
          <div className="relative flex justify-center text-xs uppercase">
            <span className="bg-background px-2 text-muted-foreground">Or continue with email</span>
          </div>
        </div>

        {/* Magic link form */}
        <form onSubmit={handleMagicLink} className="space-y-3">
          <div className="space-y-1.5">
            <label htmlFor="email" className="block text-sm font-medium">
              Email address
            </label>
            <input
              id="email"
              type="email"
              required
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              placeholder="you@example.com"
              className="w-full rounded-md border border-input bg-background px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-ring"
            />
          </div>
          <button
            type="submit"
            disabled={magicLink.isPending}
            className="w-full rounded-md bg-primary px-4 py-2.5 text-sm font-medium text-primary-foreground hover:bg-primary/90 disabled:opacity-50 transition-colors"
          >
            {magicLink.isPending ? 'Sending...' : 'Send sign-in link'}
          </button>
          {magicLink.isError && (
            <p className="text-xs text-destructive" role="alert">
              Something went wrong sending your link. Please try again.
            </p>
          )}
        </form>

        {returnUrl && (
          <p className="text-center text-xs text-muted-foreground">
            You'll be redirected back after signing in.{' '}
            <Link to="/" className="underline underline-offset-2 hover:text-foreground">
              Go to home
            </Link>
          </p>
        )}
      </div>
    </div>
  )
}
