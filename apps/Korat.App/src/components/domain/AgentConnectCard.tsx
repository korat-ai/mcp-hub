import { Link } from '@tanstack/react-router';
import { Cable } from 'lucide-react';
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from '@/components/ui/card';
import { CommandLine } from '@/components/domain/CommandLine';

/**
 * Compact instructional card shown on the Servers page when at least one server
 * exists. Teaches users how to connect an MCP client to the whole Space in one
 * command via `korat connect --space --bridge`.
 */
export function AgentConnectCard() {
  return (
    <Card size="sm">
      <CardHeader>
        <div className="flex items-center gap-2">
          <Cable className="size-4 text-muted-foreground shrink-0" />
          <CardTitle>Connect an MCP client to this Space</CardTitle>
        </div>
        <CardDescription>
          Point any MCP client at your whole Space. Every permitted server shows up behind one
          endpoint — new servers and approvals appear automatically, no reconfig.
        </CardDescription>
      </CardHeader>
      <CardContent className="flex flex-col gap-2">
        <CommandLine command="korat connect --space --bridge" />
        <div className="flex justify-end">
          <Link
            to="/servers/how_to_connect"
            className="text-xs text-muted-foreground hover:text-foreground transition-colors"
          >
            Details →
          </Link>
        </div>
      </CardContent>
    </Card>
  );
}
