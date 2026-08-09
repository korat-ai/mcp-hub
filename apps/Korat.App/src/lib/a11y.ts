// Shared row-click helper for master-list `<tr>` rows that navigate to a
// detail page on a mouse click.
//
// Fable review (#186 MEDIUM-1): this used to also stamp `role="button"` +
// `tabIndex={0}` + `onKeyDown` onto the `<tr>` (mirroring the DIV-based
// `MiniRow.tsx` pattern) to make the row keyboard-reachable. Per ARIA,
// `role="button"` is a WIDGET role — applying it to a `<tr>` strips the row
// of its native `row` semantics (cells lose their column-header
// association) and, because a focusable/actionable element should not
// contain further focusable descendants, any nested `<Link>`/button in the
// row becomes an invalid nested-interactive structure: screen readers may
// stop exposing it at all, while it remains a stray tab stop. `<tr>` is NOT
// a div — restoring real row semantics means the `<tr>` carries mouse-only
// `onClick` and keyboard/screen-reader access instead comes from a genuine
// `<Link>` in the row's primary cell (see agents.tsx / inference.tsx /
// nodes.tsx / servers.tsx).
export function rowClickProps(onActivate: () => void) {
  return {
    onClick: onActivate,
  };
}
