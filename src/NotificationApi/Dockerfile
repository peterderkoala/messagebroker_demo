FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

COPY Directory.Build.props Directory.Packages.props NotificationPlatform.slnx ./
COPY src/Contracts/NotificationPlatform.Contracts.csproj src/Contracts/
COPY src/BuildingBlocks/NotificationPlatform.BuildingBlocks.csproj src/BuildingBlocks/
COPY src/NotificationApi/NotificationPlatform.NotificationApi.csproj src/NotificationApi/
RUN dotnet restore src/NotificationApi/NotificationPlatform.NotificationApi.csproj

COPY src/Contracts/ src/Contracts/
COPY src/BuildingBlocks/ src/BuildingBlocks/
COPY src/NotificationApi/ src/NotificationApi/
RUN dotnet publish src/NotificationApi/NotificationPlatform.NotificationApi.csproj \
    -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

EXPOSE 8080
ENTRYPOINT ["dotnet", "NotificationPlatform.NotificationApi.dll"]
