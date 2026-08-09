# Product Brief: Korat MCP Hub

Last updated: 2026-05-25

> Historical product brief from the original local-runtime MVP. For the current
> model, including HTTP MCP, Space-MCP, runtime terminology, and the optional
> agent module, use [../README.md](../README.md) and
> [../ARCHITECTURE.md](../ARCHITECTURE.md).

## One-Liner

Korat MCP Hub lets users connect agents to their local MCP servers from anywhere, without VPN setup, port forwarding, or exposing local services publicly.

## Product Metaphor

Korat should feel like Tailscale for MCP.

The useful comparison is the node-joining and trust-management mechanic:

- log in on a device;
- the device joins a private space;
- local capabilities appear in that space;
- another trusted client joins the same space;
- access is granted explicitly.

Korat is not trying to be a general-purpose VPN. It is an application-layer trust and transport product for MCP.

## Problem

Users can run powerful MCP servers locally, but those servers are tied to the machine where they run. Accessing them remotely usually requires awkward or risky setup:

- port forwarding;
- VPN configuration;
- public tunnels;
- static IP or DNS work;
- custom reverse proxy setup;
- copying local setup across machines.

This creates friction for users who want an agent on one computer to use MCP capabilities on another computer.

## Target Users

Initial users:

- developers already experimenting with MCP;
- power users with multiple machines;
- users with local creative or automation tools such as Ableton, Blender, Unity, local files, local databases, or NAS systems;
- agent developers who need to test MCP servers without deploying them publicly.

The first audience can tolerate CLI-first onboarding, but will be highly sensitive to privacy, security, and transparency.

## Core Use Case

1. A user runs an MCP server on a home computer.
2. The user logs that machine into Korat.
3. The local MCP server appears in the user's private Space.
4. The user logs in from a second computer where an agent client is running.
5. The user grants the second computer's agent client access to the home MCP server.
6. The remote agent calls the local MCP server as if it were available locally.

Example:

```text
Alice's Space

Nodes:
- Mac Studio          Online
  - Ableton MCP       Published
  - Blender MCP       Published

- MacBook Pro         Online
  - Claude Desktop    Connected

Permissions:
- Claude Desktop on MacBook Pro -> Ableton MCP on Mac Studio
```

## Product Promise

The first successful user moment should be:

> An agent on my laptop successfully called an MCP tool running on my home computer.

## Payload Strategy

Korat v1 should optimize for remote MCP tool calls, not bulk data movement.

The first version should support small and medium MCP payloads and expose transfer metadata such as byte counts and session activity. It should not promise unlimited file transfer, large media movement, or bulk export workflows through the managed cloud relay.

Large payload support remains strategically important. A later product phase should make file and bulk data transfer work well, likely by adding direct node-to-node transport or another peer-to-peer-style transport mode with cloud relay fallback.

Product direction:

- **Phase 1**: reliable remote MCP tool calls through the managed relay, with explicit size limits and clear errors for oversized payloads.
- **Phase 2/3**: improve large payload and file transfer support, likely through direct encrypted node-to-node transport with fallback relay.

## Cloud MCP Strategy

A future version of Korat may expose a cloud-hosted MCP endpoint for a user's Space.

In that model, an agent client could connect to one Korat MCP endpoint and see a curated set of capabilities from the user's Space, such as local MCP servers, approved cloud integrations, and hosted connectors.

Example future direction:

```text
Agent Client
  -> Korat Space MCP
    -> Local Ableton MCP on Mac Studio
    -> Local Blender MCP on Mac Studio
    -> Gmail connector
    -> Calendar connector
    -> Other approved capabilities
```

This would move Korat toward a personal MCP hub: one trusted endpoint that aggregates local and cloud capabilities under the same identity, grant, visibility, and revocation model.

This is not part of the first version. The first version should prove trusted remote access to local MCP servers before Korat hosts or aggregates cloud MCP capabilities.

## Non-Goals For The First Version

- Build or host AI agents.
- Run model inference.
- Interpret user prompts.
- Log MCP payloads.
- Create an MCP marketplace.
- Support organizations, teams, RBAC, or enterprise SSO.
- Provide hosted MCP servers.
- Build a general-purpose VPN.
- Support public anonymous access.
- Provide unlimited file transfer or bulk data movement.
- Provide a cloud-hosted aggregated MCP endpoint.
- Provide built-in third-party cloud connectors.

## Product Principles

- Local-first capabilities, remote-friendly access.
- Explicit trust before use.
- No payload logging.
- Transparent source for user trust.
- Simple mental model: Spaces, Nodes, MCP Servers, Agent Clients, Grants.
- Make the safe path the default path.

## Open Product Questions

- What should the product vocabulary be: Space, Hub, Tailnet, Network, Workspace?
- Should the first UI be CLI-only, web-only, or CLI plus minimal web approval?
- How should users distinguish devices, MCP servers, and agent clients in UI?
- Should approvals happen from the publishing machine, the cloud UI, or both?
- ~~What license model best supports trust while protecting commercial use?~~ Answered 2026-08-09: Apache-2.0. The hosted service is the product, so neither a hosted competitor nor a proprietary derivative is worth constraining against; the patent grant is what is worth having.
- Should a later Korat Space expose one cloud MCP endpoint that aggregates local and cloud capabilities?
