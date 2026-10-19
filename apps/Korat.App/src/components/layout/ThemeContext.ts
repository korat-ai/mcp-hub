import { createContext, useContext } from 'react';
import type { Theme } from '@/hooks/useTheme';

export interface ThemeContextValue {
  theme: Theme;
  toggle: () => void;
}

export const ThemeContext = createContext<ThemeContextValue | null>(null);

export function useThemeContext(): ThemeContextValue {
  const context = useContext(ThemeContext);
  if (!context) throw new Error('useThemeContext must be used inside <ThemeProvider>');
  return context;
}
