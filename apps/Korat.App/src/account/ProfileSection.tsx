/**
 * ProfileSection — compose DisplayNameForm and ConnectedProviders.
 *
 * Receives pre-fetched `me` data from the parent (AccountLayout or test harness)
 * so it renders synchronously without its own query fetch.
 */
import type { MeDto } from '@/types/api';
import { DisplayNameForm } from '@/account/DisplayNameForm';
import { ConnectedProviders } from '@/account/ConnectedProviders';
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from '@/components/ui/card';

interface Props {
  me: MeDto;
}

export function ProfileSection({ me }: Props) {
  return (
    <div className="space-y-6">
      <Card>
        <CardHeader>
          <CardTitle>Display name</CardTitle>
        </CardHeader>
        <CardContent>
          <DisplayNameForm me={me} />
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Connected providers</CardTitle>
        </CardHeader>
        <CardContent>
          <ConnectedProviders providers={me.providers} />
        </CardContent>
      </Card>
    </div>
  );
}
