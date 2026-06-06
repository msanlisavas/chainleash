# CHAINLEASH agent — builds the Angular dashboard, then the .NET 10 agent host.
# Build context is the REPO ROOT (see docker-compose.yml) so both frontend/ and backend/
# are reachable.

# 1) Build the Angular dashboard. angular.json writes the bundle into
#    backend/ChainLeash.Agent/wwwroot (relative to the project), so it lands at
#    /src/backend/ChainLeash.Agent/wwwroot in this stage.
FROM node:20-alpine AS dashboard
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

# 3) Runtime image: published app + the built dashboard.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./
COPY --from=dashboard /src/backend/ChainLeash.Agent/wwwroot ./wwwroot
ENV ASPNETCORE_URLS=http://+:5179
EXPOSE 5179
ENTRYPOINT ["dotnet", "ChainLeash.Agent.dll"]
