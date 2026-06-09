# CHAINLEASH x402 signal provider — the "pay-to-think" seller endpoint.
# Build context is the REPO ROOT (see docker-compose.yml).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY backend/ChainLeash.SignalProvider/ChainLeash.SignalProvider.csproj backend/ChainLeash.SignalProvider/
RUN dotnet restore backend/ChainLeash.SignalProvider/ChainLeash.SignalProvider.csproj
COPY backend/ChainLeash.SignalProvider/ backend/ChainLeash.SignalProvider/
RUN dotnet publish backend/ChainLeash.SignalProvider/ChainLeash.SignalProvider.csproj -c Release -o /app/publish

# Runtime: non-root `app` user (ships with the aspnet image); curl only for the HEALTHCHECK.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish ./
ENV ASPNETCORE_URLS=http://+:5080
EXPOSE 5080
USER app
HEALTHCHECK --interval=30s --start-period=20s \
  CMD curl -fsS http://localhost:5080/ >/dev/null
ENTRYPOINT ["dotnet", "ChainLeash.SignalProvider.dll"]
