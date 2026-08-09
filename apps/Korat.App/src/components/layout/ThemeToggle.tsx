import { Sun, Moon } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { useThemeContext } from './ThemeProvider';

export function ThemeToggle() {
  const { theme, toggle } = useThemeContext();
  const next = theme === 'dark' ? 'light' : 'dark';
  return (
    <Button
      variant="ghost"
      size="icon"
      aria-label={`Switch to ${next} theme`}
      onClick={toggle}
    >
      {theme === 'dark' ? (
        <Sun className="size-4" aria-hidden="true" />
      ) : (
        <Moon className="size-4" aria-hidden="true" />
      )}
    </Button>
  );
}
