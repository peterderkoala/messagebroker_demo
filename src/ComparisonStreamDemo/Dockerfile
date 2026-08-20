# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props NotificationPlatform.slnx ./
COPY src/ComparisonStreamDemo/ src/ComparisonStreamDemo/

RUN dotnet publish src/ComparisonStreamDemo/NotificationPlatform.ComparisonStreamDemo.csproj \
    -c Release -o /app

# No ASP.NET Core surface here - the plain runtime image is enough.
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

ENTRYPOINT ["dotnet", "NotificationPlatform.ComparisonStreamDemo.dll"]
