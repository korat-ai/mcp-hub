# Korat visual assets

This directory contains the small, reusable part of the Korat visual system
that is useful to public contributors.

- `assets/` contains the current placeholder mark and icon source.
- `tokens/` contains the warm-neutral/amber palette, typography, spacing, and
  Tailwind/shadcn reference files.

The production console in `apps/Korat.App` is the source of truth for
implemented components and interaction behavior. Historical prototypes,
private positioning material, and generated design handoffs are intentionally
not part of the public release tree.

The current UI uses Geist Variable, Geist Mono, Lucide icons, warm neutrals,
and amber as its signature accent. Keep changes accessible in both light and
dark themes and avoid introducing a second visual vocabulary without a
documented product reason.
