import { Component, type ErrorInfo, type ReactNode } from 'react';
import { AppCrashScreen } from './AppCrashScreen';
import { Sentry } from '@/lib/sentry';

interface State {
  error: Error | null;
}

export class ErrorBoundary extends Component<{ children: ReactNode }, State> {
  state: State = { error: null };

  static getDerivedStateFromError(error: Error): State {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo): void {
    console.error('[korat] render crash', error, info);
    // Forward to Sentry/GlitchTip when the SDK is initialised (no-op otherwise).
    Sentry.captureException(error, { extra: { componentStack: info.componentStack } });
  }

  render(): ReactNode {
    if (this.state.error) return <AppCrashScreen error={this.state.error} />;
    return this.props.children;
  }
}
