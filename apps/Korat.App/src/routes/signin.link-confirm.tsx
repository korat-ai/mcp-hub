import { createFileRoute, useNavigate } from '@tanstack/react-router'
import { Button } from '@/components/ui/button'
import { usePendingLink, useConfirmPendingLink, useCancelPendingLink } from '@/hooks/usePendingLink'

export const Route = createFileRoute('/signin/link-confirm')({
  component: LinkConfirmPage,
})

/** Exported for test harness — allows mounting LinkConfirmPage in a minimal router. */
export { LinkConfirmPage }

function LinkConfirmPage() {
  const navigate = useNavigate()
  const pending = usePendingLink()
  const confirm = useConfirmPendingLink()
  const cancel = useCancelPendingLink()

  if (pending.isLoading) {
    return (
      <div className="flex min-h-screen items-center justify-center p-6">
        <p className="text-muted-foreground text-sm">Loading…</p>
      </div>
    )
  }

  if (!pending.data) {
    // No pending-link cookie / 404 — redirect user to sign in
    return (
      <div className="flex min-h-screen items-center justify-center p-6">
        <div className="w-full max-w-md text-center space-y-4">
          <h1 className="text-2xl font-semibold">Nothing to confirm</h1>
          <p className="text-muted-foreground text-sm">
            There is no pending account link request. Please sign in again.
          </p>
          <Button
            variant="outline"
            onClick={() => void navigate({ to: '/signin' })}
          >
            Back to sign in
          </Button>
        </div>
      </div>
    )
  }

  const { provider, email, displayName } = pending.data

  function handleConfirm() {
    confirm.mutate(undefined, {
      onSuccess: () => void navigate({ to: '/' }),
    })
  }

  function handleCancel() {
    cancel.mutate(undefined, {
      onSuccess: () => void navigate({ to: '/signin' }),
    })
  }

  return (
    <div className="flex min-h-screen items-center justify-center p-6">
      <div className="w-full max-w-md space-y-6">
        <div className="space-y-2 text-center">
          <h1 className="text-2xl font-bold tracking-tight">Link your accounts</h1>
          <p className="text-muted-foreground text-sm">
            You previously signed in with a different provider. Link your accounts to
            use either method to sign in.
          </p>
        </div>

        <div className="rounded-lg border border-border bg-muted/40 p-4 space-y-1 text-sm">
          <div className="flex justify-between">
            <span className="text-muted-foreground">New provider</span>
            <span className="font-medium capitalize">{provider}</span>
          </div>
          <div className="flex justify-between">
            <span className="text-muted-foreground">Email</span>
            <span className="font-medium">{email}</span>
          </div>
          {displayName && (
            <div className="flex justify-between">
              <span className="text-muted-foreground">Name</span>
              <span className="font-medium">{displayName}</span>
            </div>
          )}
        </div>

        <div className="space-y-3">
          <Button
            className="w-full"
            onClick={handleConfirm}
            disabled={confirm.isPending || cancel.isPending}
          >
            {confirm.isPending ? 'Linking…' : 'Yes, link my accounts'}
          </Button>
          <Button
            variant="outline"
            className="w-full"
            onClick={handleCancel}
            disabled={confirm.isPending || cancel.isPending}
          >
            {cancel.isPending ? 'Cancelling…' : 'Cancel'}
          </Button>
        </div>

        {confirm.isError && (
          <p className="text-xs text-destructive text-center" role="alert">
            We couldn't link your accounts. Please try again.
          </p>
        )}
        {cancel.isError && (
          <p className="text-xs text-destructive text-center" role="alert">
            We couldn't cancel the request. Please try again.
          </p>
        )}
      </div>
    </div>
  )
}
