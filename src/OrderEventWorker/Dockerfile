FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY global.json Directory.Build.props NuGet.Config ./
COPY src/Shared.Contracts/Shared.Contracts.csproj src/Shared.Contracts/
COPY src/Messaging/Messaging.csproj src/Messaging/
COPY src/OrderEventWorker/OrderEventWorker.csproj src/OrderEventWorker/
RUN dotnet restore src/OrderEventWorker/OrderEventWorker.csproj
COPY src/Shared.Contracts/ src/Shared.Contracts/
COPY src/Messaging/ src/Messaging/
COPY src/OrderEventWorker/ src/OrderEventWorker/
RUN dotnet publish src/OrderEventWorker/OrderEventWorker.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "OrderEventWorker.dll"]
