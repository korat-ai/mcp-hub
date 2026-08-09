# syntax=docker/dockerfile:1.7
# Korat MCP Hub — production image for apps/Korat.Cloud.
# Build runs `dotnet publish` against the Cloud entry point; runtime image is
# the ASP.NET Core slim base. One process per container: Kestrel serves REST on
# :8080 and gRPC (h2c) on :8081, and the ingress in front terminates TLS for both.
# Pin the exact SDK/runtime patch (not the floating 10.0 major.minor tag) so the image
# build is reproducible and stays in lockstep with global.json (sdk 10.0.201). Bump both
# together when moving SDK patch bands.
# NOTE: the SDK and the ASP.NET runtime base are versioned on DIFFERENT schemes — the SDK
# is 10.0.2xx (feature band) while the runtime is plain 10.0.N, and aspnet:10.0.201 does
# not exist. Do NOT guess the pairing — the SDK states it:
#   rg -o 'BundledNETCoreAppPackageVersion>([^<]+)' \
#     /usr/local/share/dotnet/sdk/10.0.201/Microsoft.NETCoreSdk.BundledVersions.props
# For 10.0.201 that is 10.0.5 — a FLOOR, not the value. The rule: never below what the SDK
# bundles, and never below the latest servicing release. This was pinned at 10.0.2 for months
# on the strength of the comment alone, shipping production three servicing releases behind —
# invisible, because patch roll-forward means the app starts happily either way.
#
# 10.0.10 deliberately runs ahead of the SDK. 10.0.8, 10.0.9 and 10.0.10 are all flagged
# security; waiting for the numbers to line up would mean leaving them unapplied. The reverse
# direction is not allowed: code built by SDK 10.0.201 runs on 10.0.10, not the other way.
ARG DOTNET_SDK_VERSION=10.0.201
ARG ASPNET_RUNTIME_VERSION=10.0.10

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK_VERSION} AS build
WORKDIR /src

# Install Node 20 for the frontend SPA build step that BuildKoratApp MSBuild target invokes.
# `npm install` (not `npm ci`) runs by default; CI builds (where $CI=true) use `npm ci`.
RUN curl -fsSL https://deb.nodesource.com/setup_20.x | bash - \
    && apt-get install -y --no-install-recommends nodejs \
    && rm -rf /var/lib/apt/lists/*

# Restore layer — copy only the manifests the Cloud subgraph touches so a code-only
# change doesn't bust the NuGet cache.
# Directory.Packages.props is REQUIRED here: central package management (ManagePackageVersionsCentrally)
# moved all versions out of the .csproj files, so without it `dotnet restore` fails NU1015 in the Docker context.
COPY Korat.slnx Directory.Build.props Directory.Packages.props ./
COPY apps/Korat.Cloud/Korat.Cloud.csproj apps/Korat.Cloud/
# This list must match the ProjectReference set of Korat.Cloud.csproj exactly: a stale
# entry fails the build with "not found" at COPY time, a missing one fails `dotnet restore`.
# Check with: rg -o 'Include="[^"]*\.csproj"' apps/Korat.Cloud/Korat.Cloud.csproj
COPY src/Korat.Domain/Korat.Domain.csproj src/Korat.Domain/
COPY src/Korat.GrainInterfaces/Korat.GrainInterfaces.csproj src/Korat.GrainInterfaces/
COPY src/Korat.Grains/Korat.Grains.csproj src/Korat.Grains/
COPY src/Korat.Mcp/Korat.Mcp.csproj src/Korat.Mcp/
COPY src/Korat.Persistence/Korat.Persistence.csproj src/Korat.Persistence/
COPY src/Korat.Protocol/Korat.Protocol.csproj src/Korat.Protocol/
RUN dotnet restore apps/Korat.Cloud/Korat.Cloud.csproj

# Frontend npm cache layer — copy package*.json first so npm install only re-runs
# when deps change (not on every source edit).
COPY apps/Korat.App/package.json apps/Korat.App/package-lock.json apps/Korat.App/
RUN cd apps/Korat.App && npm ci

# Source layer — full Cloud + frontend + dependencies.
COPY apps/Korat.Cloud/ apps/Korat.Cloud/
COPY apps/Korat.App/ apps/Korat.App/
COPY src/ src/
# Shared protocol dir (root-level): Korat.Protocol.csproj compiles ../../protocol/node-gateway.proto.
# Without this COPY the Grpc.Tools proto compile fails in the Docker build context ("No such file").
COPY protocol/ protocol/

# Set CI=true so BuildKoratApp's MSBuild target uses `npm ci` instead of `npm install`
# (idempotent + uses the lockfile). The Inputs/Outputs on the target should keep this
# fast on re-builds, but with Docker layer cache this only runs when source files change.
ENV CI=true

# Force BuildKoratApp MSBuild target to run on every Docker image build by
# injecting the BUILD_TIMESTAMP into the target's Inputs list.
ARG BUILD_TIMESTAMP=unknown
ENV BUILD_TIMESTAMP=${BUILD_TIMESTAMP}

# Git short SHA for the version footer (SPA build reads process.env.VITE_COMMIT_SHA) and
# /api/version (backend). The Docker build context has no .git, so vite's git fallback yields
# "dev" — pass it explicitly at deploy:  fly deploy --build-arg GIT_SHA=$(git rev-parse --short HEAD)
ARG GIT_SHA=dev
ENV VITE_COMMIT_SHA=${GIT_SHA}

# Optional Sentry-compatible browser error tracking — SPA build-time args.
# VITE_SENTRY_DSN: public ingest key (safe to embed in the bundle).
#   Unset → SDK is a no-op; app runs normally with zero Sentry.
#   Pass at deploy: fly deploy --build-arg VITE_SENTRY_DSN=<dsn>
# Source-map upload requires SENTRY_AUTH_TOKEN, SENTRY_ORG, SENTRY_PROJECT,
# and SENTRY_URL together. Safe to omit all four.
ARG VITE_SENTRY_DSN=
ENV VITE_SENTRY_DSN=${VITE_SENTRY_DSN}
# Optional hosted-agent/inference module. The public MCP relay console is the default.
ARG VITE_ENABLE_AGENT_PLATFORM=false
ENV VITE_ENABLE_AGENT_PLATFORM=${VITE_ENABLE_AGENT_PLATFORM}
ARG SENTRY_AUTH_TOKEN=
ENV SENTRY_AUTH_TOKEN=${SENTRY_AUTH_TOKEN}
ARG SENTRY_ORG=
ENV SENTRY_ORG=${SENTRY_ORG}
ARG SENTRY_PROJECT=
ENV SENTRY_PROJECT=${SENTRY_PROJECT}
ARG SENTRY_URL=
ENV SENTRY_URL=${SENTRY_URL}

RUN dotnet publish apps/Korat.Cloud/Korat.Cloud.csproj \
    -c Release \
    -o /publish \
    --no-restore \
    /p:UseAppHost=false \
    /p:KoratAppForceBuildTrigger=${BUILD_TIMESTAMP}

FROM mcr.microsoft.com/dotnet/aspnet:${ASPNET_RUNTIME_VERSION} AS runtime
WORKDIR /app

# libgssapi-krb5-2 is LOAD-BEARING: Npgsql loads libgssapi_krb5.so.2 at startup;
# without it the app crash-loops ("cannot open shared object file") and Kestrel
# never binds :8080. It used to come in transitively via curl — do not remove.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /publish ./

# Both listeners are cleartext: TLS is terminated by the ingress, which speaks
# HTTP/1.1 to :8080 and h2c to :8081. Kestrel binds 0.0.0.0 because the ingress
# reaches it across the pod network, not over loopback.
# Re-declare in the runtime stage (ARGs are per-stage) so /api/version can report the commit.
ARG GIT_SHA=dev
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://0.0.0.0:8080 \
    KORAT_GRPC_PORT=8081 \
    KORAT_BIND_ALL_INTERFACES=1 \
    KORAT_GIT_SHA=${GIT_SHA} \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

EXPOSE 8080
EXPOSE 8081

# dotnet as PID 1, with no tini and no wrapper script: .NET handles SIGTERM itself
# (Orleans deactivates grains and leaves the cluster before exiting), and the app
# never forks, so there are no zombies for an init to reap.
ENTRYPOINT ["dotnet", "Korat.Cloud.dll"]
