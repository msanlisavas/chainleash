# CHAINLEASH x402 signal provider — the "pay-to-think" seller endpoint.
# Build context is the REPO ROOT (see docker-compose.yml).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY backend/ChainLeash.SignalProvider/ChainLeash.SignalProvider.csproj backend/ChainLeash.SignalProvider/
RUN dotnet restore backend/ChainLeash.SignalProvider/ChainLeash.SignalProvider.csproj
COPY backend/ChainLeash.SignalProvider/ backend/ChainLeash.SignalProvider/
RUN dotnet publish backend/ChainLeash.SignalProvider/ChainLeash.SignalProvider.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./
ENV ASPNETCORE_URLS=http://+:5080
EXPOSE 5080
ENTRYPOINT ["dotnet", "ChainLeash.SignalProvider.dll"]
