# Gateway Fix Follow-ups

## Web layer (not in scope of this task)

### W-1: `/api/space` and other public GET endpoints returning 401

Several integration tests fail with `401 Unauthorized` when hitting unauthenticated GET endpoints
(`/api/space`, `/api/access-requests`, `/api/sessions`).

Root cause: `Program.cs` currently has `app.UseAuthentication()` and `app.UseAuthorization()`
removed (or never called), and `AddAuthentication` / `AddCookie` is absent. ASP.NET Core may
require these to be present for certain middleware chains to work correctly, or there is a
missing `app.UseAuthorization()` call.

Affected tests (pre-existing failures, not caused by G1–G11 fixes):
- `NodePresenceTests.RegisteredNode_AppearsInSpaceOverview`
- `NodeGatewayPresenceTests.Hello_MarksNodeOnlineInSpaceOverview`
- `NodeStalePresenceTests.NodeWithoutRecentHeartbeat_AppearsOfflineAfterStaleThreshold`
- `ConnectAccessRequestTests.PendingRequest_SurvivesAfterConnectWouldTimeout`
- `PersistenceRestartTests.PendingRequestSurvivesRestart`
- `PersistenceRestartTests.PersistedSessionRows_ContainNoPayloadFields`
- `ApprovePageContractTests.AccessRequestDetail_HasNoMcpPayloadFields`

Fix needed in `apps/Korat.Cloud/Program.cs`:
1. Restore `builder.Services.AddAuthentication(...)` and `.AddCookie()`.
2. Restore `app.UseAuthentication()` and `app.UseAuthorization()` middleware calls.
   OR verify that the custom `RequireSpaceOwner` filter works correctly without them.

### W-2: `EstablishOwnerSession` endpoint is `MapPost`, test uses `GET`

`ApprovePageContractTests.EstablishOwnerSession_SetsCookieAndStripsTokenFromExchange`
calls `client.GetAsync(...)` but the endpoint is registered with `MapPost`.

Either the endpoint should be changed to `MapGet`, or the test should use `PostAsync`.
Per the spec comment `// W5: POST instead of GET so the token never appears in a URL or access log`,
the endpoint is intentionally POST. The test needs updating.

## CLI layer (not in scope of this task)

### C-1: `SessionGrain.OpenAsync` signature change

`ISessionGrain.OpenAsync` now has a new required `SpaceId spaceId` parameter.

The 6-parameter overload is available as an extension method
(`SessionGrainExtensions.OpenAsync`) and defaults to `SpaceId("default")`.

Any CLI-side code that calls `OpenAsync` directly on a grain proxy must use the extension
method (already available via `using Korat.GrainInterfaces;`) or pass the real `SpaceId`.

### C-2: `NodeGatewayService` now requires `IConfiguration` injected

`NodeGatewayService` accepts `IConfiguration` to derive the stable gateway id.
Configure `Korat:Cloud:GatewayId` in app settings (or environment variable) to set a
custom stable id per silo. Falls back to `Environment.MachineName` if not set.

## Proto

No proto changes were made. The `GatewayToNodeMessage.empty-frame` issue (G7) was fixed
by removing the spurious empty frame write on publish success — no new proto message is needed.
