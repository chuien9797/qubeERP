FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY global.json Directory.Build.props NuGet.Config ./
COPY src/Shared.Contracts/Shared.Contracts.csproj src/Shared.Contracts/
COPY src/InventoryService/InventoryService.csproj src/InventoryService/
RUN dotnet restore src/InventoryService/InventoryService.csproj
COPY src/Shared.Contracts/ src/Shared.Contracts/
COPY src/InventoryService/ src/InventoryService/
COPY src/ApiDocumentation/ src/ApiDocumentation/
COPY src/HttpCommunication/ src/HttpCommunication/
RUN dotnet publish src/InventoryService/InventoryService.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
USER root
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=5s --start-period=30s --retries=10 CMD curl --fail --silent http://localhost:8080/health || exit 1
ENTRYPOINT ["dotnet", "InventoryService.dll"]
