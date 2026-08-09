import { describe, expect, it, vi } from 'vitest';
import { rowClickProps } from './a11y';

describe('rowClickProps', () => {
  it('calls onActivate when onClick fires', () => {
    const onActivate = vi.fn();
    const props = rowClickProps(onActivate);
    props.onClick();
    expect(onActivate).toHaveBeenCalledTimes(1);
  });

  // Fable review (#186 MEDIUM-1): a `<tr>` must stay a semantic table row — it must NOT carry
  // role="button"/tabIndex/onKeyDown (that broke column-header cell association and made nested
  // links invalid-nested-interactive, unreachable to screen readers). Keyboard/SR access now
  // comes from a real <Link> in the row's primary cell instead.
  it('does not expose role="button", tabIndex, or onKeyDown', () => {
    const props = rowClickProps(vi.fn());
    expect(props).not.toHaveProperty('role');
    expect(props).not.toHaveProperty('tabIndex');
    expect(props).not.toHaveProperty('onKeyDown');
    expect(Object.keys(props)).toEqual(['onClick']);
  });
});
