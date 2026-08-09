using System.Text.Json;
using System.Text.Json.Serialization;
using Korat.Cli.Auth;
using Korat.Cli.Commands;
using Korat.Domain.Contracts;

namespace Korat.Cli;

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for every DTO the CLI
/// serializes or deserializes. Routing all call sites through this context makes
/// JSON trim-safe: the trimmer can statically prove which types are needed and
/// retains their metadata without <c>JsonSerializerIsReflectionEnabledByDefault</c>.
/// </summary>
// PropertyNameCaseInsensitive: the cloud REST API (/api/space) serializes camelCase
// (mcpServers, displayName, …) while the local config.json is PascalCase. Case-insensitive
// matching lets the SAME context parse BOTH — without it, the camelCase API response did not
// bind to the PascalCase DTO props, so `korat mcp list` always printed "No servers" even when
// servers were published. Affects deserialization only; config is still WRITTEN PascalCase.
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(LocalIdentity))]
[JsonSerializable(typeof(LocalMcpServer))]
[JsonSerializable(typeof(AgentIdentity))]
[JsonSerializable(typeof(CliCredentials))]
[JsonSerializable(typeof(SpaceOverviewResponse))]
[JsonSerializable(typeof(McpServerDto))]
[JsonSerializable(typeof(NodeDto))]
[JsonSerializable(typeof(NodeIdDto))]
// #98/#99: machine-readable outputs for `mcp list --json` and `status --json`.
[JsonSerializable(typeof(McpListJsonEntry))]
[JsonSerializable(typeof(List<McpListJsonEntry>))]
// Increment 1 (HTTP MCP direct-to-Space): `korat mcp add-http`'s POST /api/mcp-servers body.
[JsonSerializable(typeof(McpAddHttpRequest))]
[JsonSerializable(typeof(AgentListJsonEntry))]
[JsonSerializable(typeof(List<AgentListJsonEntry>))]
[JsonSerializable(typeof(AgentListDocument))]
[JsonSerializable(typeof(StatusDocument))]
// node-visibility-doctor (2026-07-02): `korat nodes --json` + `korat node note` PATCH body.
[JsonSerializable(typeof(NodeListJsonEntry))]
[JsonSerializable(typeof(List<NodeListJsonEntry>))]
[JsonSerializable(typeof(NodeNotePatchRequest))]
// #165: `korat nodes prune` request/response.
[JsonSerializable(typeof(PruneNodesRequest))]
[JsonSerializable(typeof(PruneNodesResponse))]
// Rebrain/roster (2026-07-03): hosted-agent roster (`korat agent list`), rebrain + role
// (`korat agent rebrain` / `korat agent role`), and candidate-brain resolution against the
// existing `GET /api/inference-points`.
[JsonSerializable(typeof(AgentDto))]
[JsonSerializable(typeof(List<AgentDto>))]
[JsonSerializable(typeof(AgentBrainDto))]
[JsonSerializable(typeof(AgentRebrainPatchRequest))]
[JsonSerializable(typeof(AgentRolePatchRequest))]
// A1: `korat doctor --json` report shape.
[JsonSerializable(typeof(DoctorCommand.DoctorReport))]
[JsonSerializable(typeof(DoctorCommand.DoctorCheck))]
[JsonSerializable(typeof(List<DoctorCommand.DoctorCheck>))]
// A2: `korat doctor`'s cloud-auth check parses GET /api/auth/me for the account email.
[JsonSerializable(typeof(DoctorCommand.AuthMeDto))]
// 029: OpenAI-compatible DTOs (node-side inference provider)
internal partial class KoratCliJsonContext : JsonSerializerContext { }

/// <summary>Response body from <c>POST /api/feedback</c>.</summary>
