import { useMatch, type FileRoutesByPath } from '@tanstack/react-router';

/**
 * Guard against double-rendering on routes that also parent a `/$id` detail
 * route (dot-file nesting per TanStack Router conventions — e.g. `/servers`
 * parenting `/servers/$serverId`, `/nodes` parenting `/nodes/$name`,
 * `/inference` parenting `/inference/$pointId`). Without this guard,
 * navigating to the detail page would render the FULL master list (the list
 * route's own component) with the detail page merely appended below it via
 * Outlet, instead of replacing it. A `how_to_add` child route intentionally
 * does NOT trigger this: it's a Dialog whose visible content is portalled,
 * so the list stays correctly visible behind that overlay.
 *
 * @param from The child detail route's id, e.g. `/servers/$serverId`.
 */
export function useAtChildRoute<TFrom extends keyof FileRoutesByPath>(from: TFrom): boolean {
  return useMatch({ from, shouldThrow: false }) !== undefined;
}
