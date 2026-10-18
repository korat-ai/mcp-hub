# MCP resources and prompts

For MCP client authors and people connecting a client to a Korat Space.

The Space endpoint, `/mcp/{space}`, is one MCP server backed by the MCP servers
approved for that consumer. It supports protocol revisions `2025-06-18` and
`2025-03-26`; this feature does not upgrade the endpoint to a newer protocol.
Use the normal MCP initialize/initialized handshake and the returned session
ID. Authentication is OAuth and permissions are specific to the consumer and
the server, within one Space.

## Operations

| Operation | Behavior |
|---|---|
| `tools/list`, `tools/call` | Discover and call namespaced tools; unapproved servers expose request-access tools. |
| `resources/list` | List resources from approved servers, plus Korat's own guide. |
| `resources/templates/list` | List URI templates from approved servers. Expand them normally before reading. |
| `resources/read` | Route the resource URI to its original server; preserve text, blobs, MIME types, and metadata. |
| `resources/subscribe`, `resources/unsubscribe` | Subscribe within this MCP session when the source server supports it. |
| `prompts/list`, `prompts/get` | Discover prompts and request their messages with string arguments. |
| `completion/complete` | Route prompt/resource/resource-template argument completion to its source; return no suggestions if that source has no completion capability. |

Korat queries only capabilities the source advertises. A source can provide
resources or prompts without providing any tools. Paginated upstream catalogs
are collected into the aggregate list; aggregate lists currently have no
`nextCursor`. Repeated/invalid upstream cursors or oversized catalogs fail the
backend discovery rather than expose an incomplete catalog as complete.

## Names and URIs

Prompt names use `<server-slug>__<original-name>`. Resource URIs use
`korat://servers/<server-id>/<original-uri>`. The original URI is kept intact,
including template expressions, query strings, and fragments. For example:

```text
Source resource:  file:///project/README.md
Advertised URI:   korat://servers/abc123/file:///project/README.md
Source template:  file:///docs/{path}{?language}
Advertised URI:   korat://servers/abc123/file:///docs/{path}{?language}
Source prompt:   review
Advertised name: docs__review
```

Use the advertised values, not a namespace you construct from a display name.
Korat unwraps them only after resolving the approved source server. A client
may read an unlisted URI from an approved source (for example, one returned by
a tool). Korat does **not** interpret a namespaced URI as a Cloud filesystem
path or an HTTP address to fetch. Embedded resources and non-HTTP(S) resource
links in tool and prompt responses get the same namespace. Absolute HTTP/HTTPS resource
links remain unchanged so clients can fetch them directly. Text and structured
content remain opaque; Korat does not search and replace arbitrary URI-looking
strings.

## Changes and subscriptions

Keep a GET event stream open on the same MCP session to receive
`notifications/resources/list_changed`, `notifications/prompts/list_changed`,
and `notifications/tools/list_changed`. Re-list the corresponding catalog.

Upstream catalog changes refresh independently per source. While a refresh is
pending, lists return the last authorized snapshot; a slow source does not
delay the other sources or their change notifications. Access is checked again
before listing cached content and before accepting a refreshed catalog.

After subscribing, `notifications/resources/updated` carries the namespaced
URI; read it again for current contents. Updates are invalidations, not a
durable event history, and repeated updates may be coalesced. They are not
broadcast to other consumers or sessions. An unsubscribe or permission revoke
also removes queued updates. Subscriptions are limited to 256 per session.
If several GET streams share a session, a notification is assigned to only one
of them; event replay is not implemented.

The publisher must remain reachable. A live MCP session attempts to restore
its resource subscriptions after a backend reconnect and invalidates those
resources so the client can read them again. Restoration runs separately from
source discovery: slow watches do not prevent using its other content. A new
MCP session must subscribe again. A subscription rejected during restoration is discarded without
blocking the source's other content. Unsubscribing from a disconnected source
cancels local intent without waking it. Deleting the MCP session tears down
its backend sessions.

## Built-in guidance

Even an empty Space exposes:

- Resource `korat://help/usage`: a read-only Markdown guide to discovery,
  namespaces, access requests, and resource reads.
- Prompt `korat-guide`: usage guidance with an optional string `goal` argument.

The guide cannot grant access. Backend prompts and resources remain content
from that backend, not trusted policy or permission to perform arbitrary
actions. Server-initiated sampling, elicitation, and roots requests are not
implemented by this aggregator. Cloud-terminated sessions are not end-to-end
encrypted against the Cloud, and a backend process is not an OS sandbox.
