/**
 * CliTokensSection — card wrapper that composes CliTokenList.
 *
 * Placed at /account/cli route. No props — reads data through hooks.
 */
import { CliTokenList } from '@/account/CliTokenList';
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from '@/components/ui/card';

export function CliTokensSection() {
  return (
    <Card>
      <CardHeader>
        <CardTitle>CLI tokens</CardTitle>
      </CardHeader>
      <CardContent>
        <CliTokenList />
      </CardContent>
    </Card>
  );
}
