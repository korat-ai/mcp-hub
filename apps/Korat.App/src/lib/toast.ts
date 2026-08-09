import { toast } from 'sonner';

export type ToastTone = 'good' | 'bad';

/**
 * Mono receipt-style toast. Format: `<action> · <subject> · HH:MM`.
 * Tone drives the leading dot color (amber for good, destructive for bad).
 */
export function toastReceipt(tone: ToastTone, action: string, subject: string): void {
  const time = new Date().toLocaleTimeString([], {
    hour: '2-digit',
    minute: '2-digit',
    hour12: false,
  });
  const msg = `${action} · ${subject} · ${time}`;
  if (tone === 'good') toast.success(msg);
  else toast.error(msg);
}
