# Korat Public Roadmap

Status: directional, non-committed roadmap

Last updated: 2026-05-29

## Roadmap Thesis

Korat starts with MCP Hub because personal AI needs tools before it can become
useful in the real world.

The first step is to let an agent safely use local MCP servers from anywhere.
The next step is to make the agent reachable through convenient channels,
starting with Telegram. After that, Korat should let agents communicate with
each other through explicit human consent.

The long-term direction is a personal AI capability layer:

- your agent can use your tools;
- your agent can talk to you through the channels you already use;
- your agent can communicate with other people's agents when both sides allow
  it;
- every action stays visible, permissioned, and revocable.

Channels are not just notification surfaces. They are how personal AI becomes
available in daily life without asking the person to manage infrastructure.

This roadmap is directional. It should explain where Korat is going without
promising dates before the product is ready.

## Release Standard

Every public roadmap item should pass one internal test:

> Does this make life feel lighter for the person using AI?

For Korat, shipping is not enough. A meaningful release should remove a visible
burden: setup, switching between tools, manual coordination, uncertain
permissions, infrastructure upkeep, or repetitive back-and-forth.

The intended public reaction is:

> Korat shipped this, and I can see how it makes my life easier.

## End-State Vision

The ideal Korat user should not need to keep a home server online, maintain a
workstation, configure webhooks, or care which model provider is running their
agent.

They should be able to use the AI they already have access to, paid or free,
and connect it to useful capabilities through Korat. Over time, Korat should
remove more of the setup burden and let people interact with their agents
through convenient channels.

The end state is simple:

```text
Person says what they need
  -> their agent understands intent
  -> Korat connects approved capabilities and trusted counterparties
  -> the person sees the outcome and can inspect or revoke access
```

Korat begins with the most accessible path today: existing agent subscriptions,
local MCP servers, CLI setup, and a managed relay. That is the wedge, not the
destination.

## Now: MCP Hub

Goal:

> Connect agents to local MCP servers from anywhere, without VPN setup, port
> forwarding, or exposing local services publicly.

What this unlocks:

- publish a local MCP server from a user's machine;
- let an agent client request access to that MCP server;
- approve or deny access;
- route an MCP call through Korat;
- see publisher runtimes, MCP servers, permissions, and activity;
- revoke or disable access.

Why it matters:

MCP Hub proves Korat's core primitive: useful AI capabilities need identity,
permissions, transport, visibility, and control.

MCP Hub also lets users bring the agent products they already use. A user should
be able to connect Korat to their current agent environment and gradually add
capabilities instead of switching to an entirely new AI platform.

## Next: Channels, Telegram First

Goal:

> Let a person talk to their own agent through convenient channels, without
> manual setup.

What this should unlock:

- talk to your agent from a computer;
- talk to your agent from Telegram;
- use the same approved capabilities from each channel;
- later, add other familiar channels where users already live;
- keep the same identity, permissions, and capability model behind every
  channel;
- avoid making users configure protocols, webhooks, or local services manually.

Example:

```text
User -> Telegram -> Korat -> personal agent -> approved tools
```

Why Telegram first:

- it is familiar to many early users;
- it is good for quick intent capture;
- it works on desktop and mobile;
- it can prove that Korat is not only a developer CLI;
- it creates the natural surface for later agent-to-agent interactions.

Early success metrics:

- time to first message with personal agent;
- percent of active users who use at least one non-web channel;
- weekly active conversations per active user;
- percent of channel actions that use an approved capability;
- percent of channel users who return in week 2;
- failed-action rate with clear recovery path.

## Next: Agent-To-Agent Communication

Goal:

> Let personal agents communicate with each other when the people on both sides
> explicitly allow it.

What this should unlock:

- a person can authorize their agent to talk to another person's agent;
- agents can exchange structured messages or requests;
- the receiving side can approve, deny, or constrain what their agent may do;
- every relationship remains visible and revocable;
- no unsolicited global agent discovery is required.

Example:

```text
One person allows their agent to talk to another person's agent.
The second person allows their agent to receive those requests.

The first agent asks for available meeting times.
The second agent answers according to its owner's rules.
Both people can inspect and revoke the relationship.
```

Product principle:

Person-to-person trust comes before agent-to-agent communication.

Early success metrics:

- first successful agent-to-agent exchange;
- percent of agent-to-agent exchanges backed by explicit bilateral consent;
- time to create a trusted agent relationship;
- weekly active coordinated pairs;
- revoke completion time.

## Later: Shared Agent Scenarios

Goal:

> Let agents coordinate useful work across people, tools, and contexts without
> turning Korat into an opaque automation system.

What this may unlock:

- shared scheduling between two people;
- appointment booking, such as a haircut or recurring service;
- family or partner coordination;
- project-context coordination;
- delegated tasks that require consent from more than one person;
- service or business agents that can participate after users create demand.

The product should grow from real trusted relationships, not from a public bot
marketplace first.

Possible success metrics:

- weekly successful trusted multi-agent actions;
- completed outcomes that started from natural-language intent;
- repeat usage after first successful coordinated action;
- percent of coordinated actions completed without manual back-and-forth;
- user-reported time saved or coordination avoided;
- trust or privacy incident rate.

## Later: Capability Platform

Goal:

> Make it easier for developers and users to create new AI-accessible tools and
> scenarios without rebuilding trust, identity, transport, and permissions from
> scratch.

What this may unlock:

- more MCP-backed local capabilities;
- cloud capabilities under the same trust model;
- a future Korat Space MCP endpoint;
- third-party tools or connectors;
- reusable developer primitives for agent capabilities;
- provider-neutral use of paid and free agent products.

This stage should come after Korat has proven that users actually connect,
trust, and reuse personal AI capabilities.

Possible success metrics:

- developer time to first published capability;
- number of active capabilities per user;
- weekly successful tool-enabled AI actions;
- percent of capabilities using Korat's standard grant/session model;
- number of scenarios reused by more than one user.

## Roadmap Boundaries

Korat is not trying to become:

- a model provider;
- a general chatbot;
- a public bot marketplace first;
- a general-purpose VPN;
- a workflow DSL product;
- a walled garden for one AI provider.

Korat is building:

- a trust layer;
- a capability layer;
- a transport layer;
- channels for personal AI, beginning with Telegram;
- provider-neutral infrastructure for using the AI people already have;
- agent-to-agent communication based on human consent.
