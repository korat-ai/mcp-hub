/**
 * SecuritySection — compose EmailChangeForm and SessionsList.
 *
 * Receives pre-fetched `me` data from the parent (AccountLayout or test
 * harness) so it renders synchronously without its own me-query.
 */
import type { MeDto } from '@/types/api';
import { EmailChangeForm } from '@/account/EmailChangeForm';
import { SessionsList } from '@/account/SessionsList';
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from '@/components/ui/card';

interface Props {
  me: MeDto;
}

export function SecuritySection({ me }: Props) {
  return (
    <div className="space-y-6">
      <Card>
        <CardHeader>
          <CardTitle>Change email address</CardTitle>
        </CardHeader>
        <CardContent>
          <EmailChangeForm me={me} />
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Active sessions</CardTitle>
        </CardHeader>
        <CardContent>
          <SessionsList />
        </CardContent>
      </Card>
    </div>
  );
}
