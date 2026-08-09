#!/usr/bin/env bash
# mvp-real-mcp-demo.sh
#
# NOTE: This script predates the IdP auth cutover (task #39) and has not yet
# been updated for the new authentication model. The owner-token pattern
# (X-Korat-Owner-Token / KORAT_DEV_OWNER_SECRET) used below is removed from
# the server. This script will fail until it is updated to authenticate via
# `korat login` and Bearer CLI tokens. Tracked as a follow-up task.
#
# Formal acceptance proof: agent sends MCP initialize to @modelcontextprotocol/server-everything
# through Korat relay and captures a valid JSON-RPC response.
#
# Usage:
#   bash scripts/mvp-real-mcp-demo.sh
#
# Requirements:
#   - Docker running with Postgres on :5432  (docker compose up -d)
#   - dotnet build already run
#   - npx available (Node.js 18+)

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CLOUD_REST_PORT=5191
CLOUD_GRPC_PORT=5192
CLOUD_URL="http://127.0.0.1:${CLOUD_REST_PORT}"
OWNER_TOKEN="dev-owner-secret"

# MCP server name — unique per run to avoid 409 from FK constraints on /reset
MCP_SERVER_NAME="everything-mcp-$$"

# Temp dirs
PUB_HOME="$(mktemp -d /tmp/korat-demo-pub-XXXXXX)"
AGENT_HOME="$(mktemp -d /tmp/korat-demo-agent-XXXXXX)"
LOG_DIR="$(mktemp -d /tmp/korat-demo-logs-XXXXXX)"

PUB_CONFIG="${PUB_HOME}/config.json"
AGENT_CONFIG="${AGENT_HOME}/config.json"

CLOUD_LOG="${LOG_DIR}/cloud.log"
PUB_LOG="${LOG_DIR}/publisher.log"
AGENT_LOG="${LOG_DIR}/agent.log"

CLOUD_PID=""
PUB_PID=""

# ── Cleanup ──────────────────────────────────────────────────────────────────
cleanup() {
  echo ""
  echo "Cleaning up..."
  [[ -n "${PUB_PID}" ]] && kill "${PUB_PID}" 2>/dev/null || true
  [[ -n "${CLOUD_PID}" ]] && kill "${CLOUD_PID}" 2>/dev/null || true
  # Give processes a moment to exit cleanly
  sleep 1
  [[ -n "${PUB_PID}" ]] && kill -9 "${PUB_PID}" 2>/dev/null || true
  [[ -n "${CLOUD_PID}" ]] && kill -9 "${CLOUD_PID}" 2>/dev/null || true
}
trap cleanup EXIT

fail() {
  echo ""
  echo "FAIL: $1"
  echo ""
  echo "--- Cloud log (last 50 lines) ---"
  tail -50 "${CLOUD_LOG}" 2>/dev/null || echo "(empty)"
  echo ""
  echo "--- Publisher log (last 50 lines) ---"
  tail -50 "${PUB_LOG}" 2>/dev/null || echo "(empty)"
  echo ""
  echo "--- Agent log (last 50 lines) ---"
  tail -50 "${AGENT_LOG}" 2>/dev/null || echo "(empty)"
  exit 1
}

# ── Step 1: Verify npx + MCP server availability ──────────────────────────────
echo "Step 1: Verifying npx and @modelcontextprotocol/server-everything..."
NPX_PATH="$(which npx)" || fail "npx not found. Install Node.js 18+ first."
echo "  npx: ${NPX_PATH} ($(npx --version))"

# Quick smoke test using Node.js to drive the MCP server with a 20s timeout
echo "  Testing MCP server startup (first run may download package, allow 60s)..."
MCP_TEST_OUTPUT="$(node -e "
const { spawn } = require('child_process');
const proc = spawn('npx', ['-y', '@modelcontextprotocol/server-everything', 'stdio'], {
  stdio: ['pipe', 'pipe', 'pipe']
});
let stdout = '';
proc.stdout.on('data', d => { stdout += d.toString(); });
const msg = JSON.stringify({jsonrpc:'2.0',id:1,method:'initialize',params:{protocolVersion:'2024-11-05',capabilities:{},clientInfo:{name:'test',version:'1.0'}}}) + '\n';
proc.stdin.write(msg);
setTimeout(() => {
  process.stdout.write(stdout.split('\n')[0] || '');
  proc.kill();
  process.exit(0);
}, 25000);
" 2>/dev/null)" || fail "MCP server smoke test failed"

echo "${MCP_TEST_OUTPUT}" | grep -q '"jsonrpc"' || fail "MCP server smoke test: no JSON-RPC response. Got: ${MCP_TEST_OUTPUT}"
echo "  MCP server OK. Smoke response: ${MCP_TEST_OUTPUT:0:120}..."

# ── Step 2: Ensure Postgres is reachable ─────────────────────────────────────
echo ""
echo "Step 2: Verifying Postgres on :5432..."
if ! nc -z 127.0.0.1 5432 2>/dev/null; then
  fail "Postgres not reachable on localhost:5432. Run: docker compose up -d"
fi
echo "  Postgres OK."

# ── Step 3: Start Cloud ───────────────────────────────────────────────────────
echo ""
echo "Step 3: Starting Korat Cloud on ports ${CLOUD_REST_PORT} (REST) / ${CLOUD_GRPC_PORT} (gRPC)..."

# Kill any existing process on our ports
lsof -ti tcp:${CLOUD_REST_PORT} 2>/dev/null | xargs kill -9 2>/dev/null || true
lsof -ti tcp:${CLOUD_GRPC_PORT} 2>/dev/null | xargs kill -9 2>/dev/null || true
sleep 0.5

ASPNETCORE_ENVIRONMENT=Development \
ASPNETCORE_URLS="http://127.0.0.1:${CLOUD_REST_PORT}" \
KORAT_GRPC_PORT="${CLOUD_GRPC_PORT}" \
dotnet run --no-build --no-launch-profile --project "${REPO_ROOT}/apps/Korat.Cloud" \
  >"${CLOUD_LOG}" 2>&1 &
CLOUD_PID=$!
echo "  Cloud PID: ${CLOUD_PID}"

# Wait up to 60s for Cloud REST to respond
echo "  Waiting for Cloud /api/developer to respond (up to 60s)..."
CLOUD_READY=false
for i in $(seq 1 60); do
  if curl -sf -o /dev/null "${CLOUD_URL}/api/developer" 2>/dev/null; then
    CLOUD_READY=true
    break
  fi
  sleep 1
done
"${CLOUD_READY}" || fail "Cloud did not start within 60s"
echo "  Cloud is ready."

# Reset state for clean slate
curl -sf -X POST "${CLOUD_URL}/api/developer/reset" \
  -H "X-Korat-Owner-Token: ${OWNER_TOKEN}" \
  -o /dev/null || fail "Failed to reset Cloud state"
echo "  Cloud state reset."

# ── Step 4: Write publisher config ───────────────────────────────────────────
echo ""
echo "Step 4: Writing publisher config..."
PUB_NODE_ID="$(node -e "const c=require('crypto');process.stdout.write(c.randomBytes(16).toString('hex'))")"

# Escape NPX_PATH for JSON
NPX_JSON="${NPX_PATH}"

cat > "${PUB_CONFIG}" <<EOF
{
  "SpaceId": "default",
  "NodeId": "${PUB_NODE_ID}",
  "CloudUrl": "${CLOUD_URL}",
  "CloudGrpcUrl": "http://127.0.0.1:${CLOUD_GRPC_PORT}",
  "OwnerToken": "${OWNER_TOKEN}",
  "McpServers": [
    {
      "DisplayName": "${MCP_SERVER_NAME}",
      "LaunchCommand": "${NPX_JSON}",
      "LaunchArguments": "-y @modelcontextprotocol/server-everything stdio"
    }
  ]
}
EOF
echo "  Publisher config: ${PUB_CONFIG}"
echo "  Publisher NodeId: ${PUB_NODE_ID}"

# ── Step 5: Start publisher CLI (korat up --serve everything-mcp) ─────────────
echo ""
echo "Step 5: Starting publisher CLI (korat up --serve ${MCP_SERVER_NAME})..."

KORAT_CONFIG="${PUB_CONFIG}" HOME="${PUB_HOME}" \
dotnet run --no-build --project "${REPO_ROOT}/apps/Korat.Cli" \
  -- up --serve "${MCP_SERVER_NAME}" \
  >"${PUB_LOG}" 2>&1 &
PUB_PID=$!
echo "  Publisher PID: ${PUB_PID}"

# Poll until publisher node is Online in Space (up to 30s)
echo "  Waiting for publisher node to come online in Space (up to 30s)..."
PUB_ONLINE=false
for i in $(seq 1 30); do
  SPACE_JSON="$(curl -sf "${CLOUD_URL}/api/space" \
    -H "X-Korat-Owner-Token: ${OWNER_TOKEN}" 2>/dev/null || echo '{}')"
  if echo "${SPACE_JSON}" | grep -q "${PUB_NODE_ID}" && echo "${SPACE_JSON}" | grep -q '"Online"'; then
    PUB_ONLINE=true
    break
  fi
  sleep 1
done
"${PUB_ONLINE}" || fail "Publisher node did not come online within 30s. Publisher log: $(tail -20 ${PUB_LOG})"
echo "  Publisher node online."

# ── Step 6: Register MCP server, agent-client, and grant via developer API ────
echo ""
echo "Step 6: Setting up trust scenario via /api/developer..."

# Register MCP server (binds to pubNodeId)
SERVER_RESP="$(curl -sf -X POST "${CLOUD_URL}/api/developer/mcp-servers" \
  -H "X-Korat-Owner-Token: ${OWNER_TOKEN}" \
  -H "Content-Type: application/json" \
  -d "{\"displayName\":\"${MCP_SERVER_NAME}\",\"nodeId\":\"${PUB_NODE_ID}\"}")" \
  || fail "Failed to register MCP server via /api/developer/mcp-servers"
SERVER_ID="$(echo "${SERVER_RESP}" | node -e "let d='';process.stdin.on('data',c=>d+=c).on('end',()=>process.stdout.write(JSON.parse(d).id||''))")"
[[ -n "${SERVER_ID}" ]] || fail "MCP server registration returned empty id. Response: ${SERVER_RESP}"
echo "  MCP server id: ${SERVER_ID}"

# Register a node for the agent CLI to use. The agent CLI will Hello as this nodeId,
# so the agent-client we create below must be registered with the SAME sourceNodeId —
# the gateway's ARCH-CRITICAL-2 trust gate rejects any RequestSession whose
# agent-client.NodeId does not match the stream's Hello-bound NodeId.
AGENT_NODE_RESP="$(curl -sf -X POST "${CLOUD_URL}/api/developer/nodes" \
  -H "X-Korat-Owner-Token: ${OWNER_TOKEN}" \
  -H "Content-Type: application/json" \
  -d '{"displayName":"demo-agent-node"}')" \
  || fail "Failed to register agent node via /api/developer/nodes"
AGENT_NODE_ID="$(echo "${AGENT_NODE_RESP}" | node -e "let d='';process.stdin.on('data',c=>d+=c).on('end',()=>process.stdout.write(JSON.parse(d).id||''))")"
[[ -n "${AGENT_NODE_ID}" ]] || fail "Agent node registration returned empty id. Response: ${AGENT_NODE_RESP}"
echo "  Agent node id: ${AGENT_NODE_ID}"

# Register agent-client, binding it to the agent node so the gateway's trust check passes.
AGENT_RESP="$(curl -sf -X POST "${CLOUD_URL}/api/developer/agent-clients" \
  -H "X-Korat-Owner-Token: ${OWNER_TOKEN}" \
  -H "Content-Type: application/json" \
  -d "{\"displayName\":\"demo-agent\",\"sourceNodeId\":\"${AGENT_NODE_ID}\"}")" \
  || fail "Failed to register agent-client"
AGENT_CLIENT_ID="$(echo "${AGENT_RESP}" | node -e "let d='';process.stdin.on('data',c=>d+=c).on('end',()=>process.stdout.write(JSON.parse(d).id||''))")"
[[ -n "${AGENT_CLIENT_ID}" ]] || fail "Agent-client registration returned empty id. Response: ${AGENT_RESP}"
echo "  Agent-client id: ${AGENT_CLIENT_ID}"

# Create grant
GRANT_STATUS="$(curl -sf -o /dev/null -w "%{http_code}" -X POST "${CLOUD_URL}/api/developer/grants" \
  -H "X-Korat-Owner-Token: ${OWNER_TOKEN}" \
  -H "Content-Type: application/json" \
  -d "{\"agentClientId\":\"${AGENT_CLIENT_ID}\",\"mcpServerId\":\"${SERVER_ID}\"}")"
[[ "${GRANT_STATUS}" =~ ^2 ]] || fail "Grant creation failed with HTTP ${GRANT_STATUS}"
echo "  Grant created (HTTP ${GRANT_STATUS})."

# ── Step 7: Write agent config ────────────────────────────────────────────────
echo ""
echo "Step 7: Writing agent config..."
# Reuse the node id we registered above so the agent CLI's Hello-bound NodeId
# matches the agent-client's recorded NodeId.

cat > "${AGENT_CONFIG}" <<EOF
{
  "SpaceId": "default",
  "NodeId": "${AGENT_NODE_ID}",
  "CloudUrl": "${CLOUD_URL}",
  "CloudGrpcUrl": "http://127.0.0.1:${CLOUD_GRPC_PORT}",
  "OwnerToken": "${OWNER_TOKEN}"
}
EOF
echo "  Agent config: ${AGENT_CONFIG}"
echo "  Agent NodeId: ${AGENT_NODE_ID}"

# ── Step 8: Run agent CLI with MCP initialize request ─────────────────────────
echo ""
echo "Step 8: Running korat connect --send '<MCP initialize>' --wait-response..."
echo "  (First call to npx on publisher side may take 30-90s to download @modelcontextprotocol/server-everything)"
echo "  Allowing 120s for the round-trip..."

MCP_INIT_MSG='{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"korat-demo-agent","version":"1.0"}}}'

# Run with a 120s timeout using Perl (available on macOS without extra install)
AGENT_OUTPUT_FILE="${LOG_DIR}/agent-output.txt"
AGENT_EXIT_CODE=0

KORAT_CONNECT_APPROVAL_TIMEOUT_SECONDS=120 \
KORAT_CONFIG="${AGENT_CONFIG}" HOME="${AGENT_HOME}" \
dotnet run --no-build --project "${REPO_ROOT}/apps/Korat.Cli" \
  -- connect "${MCP_SERVER_NAME}" \
     --send "${MCP_INIT_MSG}" \
     --wait-response \
     --agent-client-id "${AGENT_CLIENT_ID}" \
  2>"${AGENT_LOG}" | tee "${AGENT_OUTPUT_FILE}" || AGENT_EXIT_CODE=$?

echo ""
echo "  Agent exit code: ${AGENT_EXIT_CODE}"
AGENT_STDOUT="$(cat "${AGENT_OUTPUT_FILE}")"

# ── Step 9: Verify the response ───────────────────────────────────────────────
echo ""
echo "Step 9: Verifying JSON-RPC response..."

if echo "${AGENT_STDOUT}" | grep -q '"jsonrpc"'; then
  # Extract the JSON line from stdout (skip "Access granted..." line)
  JSON_RESPONSE="$(echo "${AGENT_STDOUT}" | grep '"jsonrpc"' | head -1)"
  echo ""
  echo "============================================================"
  echo "MVP REAL-MCP DEMO PASSED"
  echo "============================================================"
  echo ""
  echo "Captured MCP response from @modelcontextprotocol/server-everything through Korat relay:"
  echo ""
  echo "${JSON_RESPONSE}"
  echo ""
  echo "Full agent output:"
  echo "${AGENT_STDOUT}"
  echo "============================================================"
  exit 0
else
  fail "No JSON-RPC response found in agent output.\n\nAgent stdout:\n${AGENT_STDOUT}\n\nAgent stderr:\n$(cat ${AGENT_LOG})"
fi
