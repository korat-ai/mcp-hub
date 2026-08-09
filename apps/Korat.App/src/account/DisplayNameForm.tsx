/**
 * DisplayNameForm — let the user update their display name.
 *
 * Rules:
 *  - Display name must not be blank (client-side guard before calling the API).
 *  - Submit is disabled while the mutation is pending (no double-submit).
 *  - On success, inline confirmation is shown and auth.me is invalidated via
 *    useUpdateMe (spec §3.1).
 *  - On API error, the error message is surfaced inline.
 */
import { useState } from 'react';
import type { MeDto } from '@/types/api';
import { useUpdateMe } from '@/account/hooks';
import { ApiError } from '@/lib/api';

interface Props {
  me: MeDto;
}

export function DisplayNameForm({ me }: Props) {
  const [value, setValue] = useState(me.displayName ?? '');
  const [localError, setLocalError] = useState<string | null>(null);
  const [success, setSuccess] = useState(false);

  const update = useUpdateMe();

  function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setLocalError(null);
    setSuccess(false);

    const trimmed = value.trim();
    if (!trimmed) {
      setLocalError('Display name cannot be blank.');
      return;
    }

    update.mutate(
      { displayName: trimmed },
      {
        onSuccess: () => setSuccess(true),
        onError: (err) => {
          setLocalError(
            err instanceof ApiError ? err.message : 'Something went wrong. Please try again.',
          );
        },
      },
    );
  }

  const errorMessage = localError ?? (update.isError
    ? (update.error instanceof ApiError ? update.error.message : 'Something went wrong.')
    : null);

  return (
    <form onSubmit={handleSubmit} className="space-y-3">
      <div className="space-y-1.5">
        <label htmlFor="display-name" className="block text-sm font-medium">
          Display name
        </label>
        <input
          id="display-name"
          type="text"
          value={value}
          onChange={(e) => {
            setValue(e.target.value);
            setLocalError(null);
            setSuccess(false);
          }}
          maxLength={100}
          className="w-full rounded-md border border-input bg-background px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-ring"
          aria-describedby={errorMessage ? 'display-name-error' : undefined}
        />
        {errorMessage && (
          <p
            id="display-name-error"
            role="alert"
            className="text-xs text-destructive"
          >
            {errorMessage}
          </p>
        )}
        {success && !errorMessage && (
          <p className="text-xs text-green-600 dark:text-green-400">
            Display name updated.
          </p>
        )}
      </div>
      <button
        type="submit"
        disabled={update.isPending}
        className="inline-flex items-center rounded-md bg-primary px-3 py-1.5 text-sm font-medium text-primary-foreground hover:bg-primary/90 disabled:opacity-50 transition-colors"
        aria-label="Save"
      >
        {update.isPending ? 'Saving...' : 'Save'}
      </button>
    </form>
  );
}
