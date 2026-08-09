# Licensing notes

Last updated: 2026-08-09

## Current state

The project is licensed under the **Apache License 2.0**. The canonical text is
in [`../LICENSE`](../LICENSE); the copyright holder is Korat AI.

That grants everyone the right to use, modify, redistribute, self-host, and
build derivatives — commercially included, proprietary derivatives included —
provided the license and notices travel with the code and modified files are
marked as changed.

## Why Apache-2.0

- **Not MIT/BSD.** Equally permissive, but Apache-2.0 adds an explicit patent
  grant (§3): a contributor cannot hand over code and later assert a patent
  against the people using it. MIT is silent on patents. For a wire protocol
  meant to be implemented by other people, that guarantee is the point.
- **Not GPL.** Copyleft was considered and declined by the owner. Its
  reciprocity applies on *distribution*, so it would have constrained ordinary
  adopters and third-party client authors while leaving the hosted-competitor
  case — the one it gets reached for — untouched.
- **Not AGPL.** AGPL does close that case, and it was declined for the same
  reason: a hosted competitor is an accepted outcome, and AGPL's cost falls on
  adopters, many of whom cannot take an AGPL dependency at all.
- **Not source-available.** A commercial-use restriction is not an open-source
  license and would not be described as one.

The business model this rests on is that the product is the *hosted service* —
the relay network, its operations, its trust posture — not exclusive possession
of the source.

## What the license settles

- Self-hosting is permitted explicitly, without asking.
- Hosted competitors may use this code.
- Proprietary derivatives are permitted.
- A patent grant is included.
- Contributions are inbound=outbound under §5. No CLA, no DCO — opening a pull
  request licenses the contribution on the same terms.
- **Third-party clients are unconstrained.** Anyone may implement the wire
  protocol from [`../protocol/SPEC.md`](../protocol/SPEC.md) and
  [`../protocol/CRYPTO.md`](../protocol/CRYPTO.md), or link the reference
  implementation in `src/Korat.Protocol`, under any license they choose. Under
  a copyleft license this would have needed a separate, more permissive license
  for that directory; under Apache-2.0 it needs nothing.

## What it does not settle

- Whether the optional agent-platform module ships in the same distribution.
- **Dependency compatibility — not reviewed.** Apache-2.0 is outbound-compatible
  with MIT/BSD and can be consumed by GPLv3, but a GPL-licensed dependency
  inside this tree would be a real conflict. No such dependency is known to be
  present; no formal pass has been run.
- Trademark. Apache-2.0 §6 grants no trademark rights, so the name and marks
  are not licensed with the code.

## Release-gate status

1. ~~Replace the placeholder with canonical license text.~~ Done — `LICENSE`.
2. ~~Update README and CONTRIBUTING language.~~ Done.
3. ~~Add package/repository license metadata.~~ Done — `package.json` manifests
   carry `"license": "Apache-2.0"`; GitHub detects `LICENSE` automatically. No
   NuGet packages are produced, so there is no `PackageLicenseExpression`.
4. **Dependency license review — not run.** The one gate this did not close.
5. Decision recorded in [decision-log.md](decision-log.md).
