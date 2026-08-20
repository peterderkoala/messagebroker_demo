# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props NotificationPlatform.slnx ./
COPY src/Contracts/ src/Contracts/
COPY src/BuildingBlocks/ src/BuildingBlocks/
COPY src/SmsChannelService/ src/SmsChannelService/

RUN dotnet publish src/SmsChannelService/NotificationPlatform.SmsChannelService.csproj \
    -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

ENTRYPOINT ["dotnet", "NotificationPlatform.SmsChannelService.dll"]
