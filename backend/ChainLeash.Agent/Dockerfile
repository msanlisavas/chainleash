# CHAINLEASH agent — builds the Angular dashboard, then the .NET 10 agent host.
# Build context is the REPO ROOT (see docker-compose.yml) so both frontend/ and backend/
# are reachable.

# 1) Build the Angular dashboard. angular.json writes the bundle into
#    backend/ChainLeash.Agent/wwwroot (relative to the project), so it lands at
#    /src/backend/ChainLeash.Agent/wwwroot in this stage.
FROM node:24-alpine AS dashboard
WORKDIR /src/frontend/dashboard
COPY frontend/dashboard/package*.json ./
RUN npm ci
COPY frontend/dashboard/ ./
RUN npx ng build

# 2) Restore + publish the agent.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY backend/ChainLeash.Agent/ChainLeash.Agent.csproj backend/ChainLeash.Agent/
RUN dotnet restore backend/ChainLeash.Agent/ChainLeash.Agent.csproj
COPY backend/ChainLeash.Agent/ backend/ChainLeash.Agent/
RUN dotnet publish backend/ChainLeash.Agent/ChainLeash.Agent.csproj -c Release -o /app/publish

# 3) Runtime image: published app + the built dashboard. Runs as the non-root `app`
#    user the aspnet image ships; curl is only there for the container HEALTHCHECK.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/* \
 # the agent persists its audit feed under /data (compose named volume) — must be app-writable
 && mkdir -p /data && chown app:app /data
COPY --from=build /app/publish ./
COPY --from=dashboard /src/backend/ChainLeash.Agent/wwwroot ./wwwroot
ENV ASPNETCORE_URLS=http://+:5179
EXPOSE 5179
USER app
# Process-liveness only — /healthz does ZERO chain reads, so the 30s probe can't drain the
# node-RPC quota. (compose overrides this with the same /healthz probe; this covers a bare
# `docker run`.) A chain outage surfaces via the agent's HOLD/Stale + /health, not a restart.
HEALTHCHECK --interval=30s --start-period=20s \
  CMD curl -fsS http://localhost:5179/healthz >/dev/null
ENTRYPOINT ["dotnet", "ChainLeash.Agent.dll"]
