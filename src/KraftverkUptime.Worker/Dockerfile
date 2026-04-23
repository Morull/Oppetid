# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props global.json ./
COPY src/KraftverkUptime.Core/KraftverkUptime.Core.csproj src/KraftverkUptime.Core/
COPY src/KraftverkUptime.Infrastructure/KraftverkUptime.Infrastructure.csproj src/KraftverkUptime.Infrastructure/
COPY src/KraftverkUptime.Worker/KraftverkUptime.Worker.csproj src/KraftverkUptime.Worker/
COPY src/KraftverkUptime.Modules.Settlement/KraftverkUptime.Modules.Settlement.csproj src/KraftverkUptime.Modules.Settlement/
COPY src/KraftverkUptime.Modules.Classification/KraftverkUptime.Modules.Classification.csproj src/KraftverkUptime.Modules.Classification/
COPY src/KraftverkUptime.Modules.Reporting/KraftverkUptime.Modules.Reporting.csproj src/KraftverkUptime.Modules.Reporting/
RUN dotnet restore src/KraftverkUptime.Worker/KraftverkUptime.Worker.csproj

COPY src/ src/
RUN dotnet publish src/KraftverkUptime.Worker/KraftverkUptime.Worker.csproj \
    -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

RUN apt-get update \
    && apt-get install -y --no-install-recommends tzdata \
    && rm -rf /var/lib/apt/lists/* \
    && ln -sf /usr/share/zoneinfo/Europe/Oslo /etc/localtime

COPY --from=build /app ./
ENTRYPOINT ["dotnet", "KraftverkUptime.Worker.dll"]
