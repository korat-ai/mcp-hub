// SecretInput — a password-type text input with an Eye/EyeOff reveal toggle, reusing the
// $pointId IssuedKeyModal's reveal idiom. Used for write-only secret fields (byok provider
// API key, byo_endpoint auth header value): the caller owns the value in local state and is
// responsible for clearing it after a successful submit — this component never persists or
// echoes the value anywhere else (no logging, no default value from a "current" secret).
import { useId, useState } from 'react';
import { Eye, EyeOff } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';

interface SecretInputProps {
  id?: string;
  value: string;
  onChange: (value: string) => void;
  placeholder?: string;
  disabled?: boolean;
  className?: string;
  'aria-describedby'?: string;
}

export function SecretInput({
  id,
  value,
  onChange,
  placeholder,
  disabled,
  className,
  ...rest
}: SecretInputProps) {
  const [visible, setVisible] = useState(false);
  const autoId = useId();
  const inputId = id ?? autoId;

  return (
    <div
      className={cn(
        'flex items-center gap-1 rounded-md border border-input bg-background pr-1 focus-within:ring-2 focus-within:ring-ring',
        className,
      )}
    >
      <input
        id={inputId}
        type={visible ? 'text' : 'password'}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        placeholder={placeholder}
        disabled={disabled}
        // autoComplete="new-password" is a deliberate trade-off: it's the strongest signal
        // browsers honour to STOP suggesting/autofilling a saved credential into this field
        // (autoComplete="off" is widely ignored by password managers), at the cost of also
        // blocking legitimate autofill of an already-saved value. The browser may still offer
        // to SAVE the entered value as a new credential on submit — that prompt is outside
        // this component's control (no standards-track way to suppress it) and is accepted.
        autoComplete="new-password"
        spellCheck={false}
        className="min-w-0 flex-1 bg-transparent px-3 py-2 text-sm outline-none disabled:opacity-50"
        {...rest}
      />
      <Button
        type="button"
        variant="ghost"
        size="icon-sm"
        tabIndex={-1}
        onClick={() => setVisible((v) => !v)}
        aria-label={visible ? 'Hide value' : 'Reveal value'}
      >
        {visible ? <EyeOff className="size-3.5" /> : <Eye className="size-3.5" />}
      </Button>
    </div>
  );
}
