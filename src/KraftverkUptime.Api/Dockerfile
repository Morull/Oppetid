# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Kopier csproj først for bedre lag-caching
COPY Directory.Build.props global.json ./
COPY src/KraftverkUptime.Core/KraftverkUptime.Core.csproj src/KraftverkUptime.Core/
COPY src/KraftverkUptime.Infrastructure/KraftverkUptime.Infrastructure.csproj src/KraftverkUptime.Infrastructure/
COPY src/KraftverkUptime.Api/KraftverkUptime.Api.csproj src/KraftverkUptime.Api/
COPY src/KraftverkUptime.Modules.Settlement/KraftverkUptime.Modules.Settlement.csproj src/KraftverkUptime.Modules.Settlement/
COPY src/KraftverkUptime.Modules.Classification/KraftverkUptime.Modules.Classification.csproj src/KraftverkUptime.Modules.Classification/
COPY src/KraftverkUptime.Modules.Reporting/KraftverkUptime.Modules.Reporting.csproj src/KraftverkUptime.Modules.Reporting/
COPY src/KraftverkUptime.Modules.Annotations/KraftverkUptime.Modules.Annotations.csproj src/KraftverkUptime.Modules.Annotations/
COPY src/KraftverkUptime.Modules.Scada/KraftverkUptime.Modules.Scada.csproj src/KraftverkUptime.Modules.Scada/
RUN dotnet restore src/KraftverkUptime.Api/KraftverkUptime.Api.csproj

COPY src/ src/
RUN dotnet publish src/KraftverkUptime.Api/KraftverkUptime.Api.csproj \
    -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

RUN apt-get update \
    && apt-get install -y --no-install-recommends tzdata curl \
    && rm -rf /var/lib/apt/lists/* \
    && ln -sf /usr/share/zoneinfo/Europe/Oslo /etc/localtime

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl -fsS http://localhost:8080/health/live || exit 1

COPY --from=build /app ./
ENTRYPOINT ["dotnet", "KraftverkUptime.Api.dll"]
