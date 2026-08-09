namespace Korat.Domain;

/// <summary>
/// Increment 1 (HTTP MCP direct-to-Space): McpServer.Transport discriminator values.
/// The column already existed (reserved, unwired — see McpServerRecord.Transport doc comment)
/// with every existing row storing the legacy literal "Stdio". This increment adds exactly one
/// new recognized value; anything else (including "Stdio") is treated as stdio_node by the
/// application layer — no backfill/data migration of existing rows.
/// </summary>
public static class McpServerTransports
{
    public const string HttpCloud = "http_cloud";

    public static bool IsHttpCloud(string transport) => transport == HttpCloud;
}

/// <summary>
/// Increment 1 introduced None/Bearer/Header; Increment 2 (HTTP MCP OAuth) unblocks Oauth —
/// see IsValid below and the increment-2 plan's Grounding Notes for why the two increment-1
/// tests that asserted "oauth" was rejected were edited, not left red.
/// </summary>
public static class McpServerAuthModes
{
    public const string None = "none";
    public const string Bearer = "bearer";
    public const string Header = "header";
    public const string Oauth = "oauth";

    public static bool IsValid(string mode) => mode is None or Bearer or Header or Oauth;

    /// <summary>Null-safe: McpServer.AuthMode is nullable (null for stdio_node servers) — every
    /// caller of this helper reads that nullable field directly (StateTransitions.EnableMcpServer,
    /// HttpMcpProxyGrain, the PATCH edit-path), so this must not throw on null.</summary>
    public static bool IsOAuth(string? mode) => mode == Oauth;
}
