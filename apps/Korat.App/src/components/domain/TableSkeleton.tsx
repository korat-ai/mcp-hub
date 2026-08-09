import {
  Table, TableBody, TableCell, TableHead, TableHeader, TableRow,
} from '@/components/ui/table';
import { Skeleton } from '@/components/ui/skeleton';

interface Props {
  headers: string[];
  rows?: number;
}

/**
 * Loading-state skeleton for tables that follow the eyebrow-header pattern.
 * Pass the same headers array that the real table will use to preserve column
 * widths during the swap (no layout shift on first paint).
 */
export function TableSkeleton({ headers, rows = 3 }: Props) {
  return (
    <Table>
      <TableHeader>
        <TableRow>
          {headers.map((h, i) => <TableHead key={i} className="eyebrow">{h}</TableHead>)}
        </TableRow>
      </TableHeader>
      <TableBody>
        {Array.from({ length: rows }).map((_, r) => (
          <TableRow key={r}>
            {headers.map((_, c) => <TableCell key={c}><Skeleton className="h-4" /></TableCell>)}
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}
