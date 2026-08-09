/**
 * Optional product modules.
 *
 * The open-source/default console is the MCP trust + relay product described in README.
 * Hosted inference/agents/rooms/channels remain available for deployments that explicitly opt in.
 */
export const agentPlatformEnabled =
  import.meta.env.VITE_ENABLE_AGENT_PLATFORM === 'true';
