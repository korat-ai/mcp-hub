// Shared copy-to-clipboard button with a check-mark confirmation. Promoted
// from the inline CopyButton previously defined in routes/inference.tsx so
// list cells, detail base-URL/snippet blocks, IssuedKeyModal, and how-to
// steps can all share one implementation.
import { useState } from 'react';
import { Check, Copy } from 'lucide-react';
import { Button } from '@/components/ui/button';

export interface CopyButtonProps {
  value: string;
  label?: string;
  /** How long the check-mark confirmation stays visible, in ms. */
  confirmMs?: number;
}

export function CopyButton({ value, label = 'Copy', confirmMs = 1500 }: CopyButtonProps) {
  const [copied, setCopied] = useState(false);

  const handleCopy = () => {
    void navigator.clipboard.writeText(value).then(() => {
      setCopied(true);
      setTimeout(() => setCopied(false), confirmMs);
    });
  };

  return (
    <Button variant="ghost" size="sm" onClick={handleCopy} aria-label={label}>
      {copied ? <Check className="size-3.5 text-primary" /> : <Copy className="size-3.5" />}
    </Button>
  );
}
