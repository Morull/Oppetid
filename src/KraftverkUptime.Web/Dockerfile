# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props global.json ./
COPY src/KraftverkUptime.Core/KraftverkUptime.Core.csproj src/KraftverkUptime.Core/
COPY src/KraftverkUptime.Web/KraftverkUptime.Web.csproj src/KraftverkUptime.Web/
RUN dotnet restore src/KraftverkUptime.Web/KraftverkUptime.Web.csproj

COPY src/ src/
RUN dotnet publish src/KraftverkUptime.Web/KraftverkUptime.Web.csproj -c Release -o /app --no-restore

# Nginx serverer statiske WASM-ressurser
FROM nginx:1.27-alpine AS runtime
COPY --from=build /app/wwwroot /usr/share/nginx/html

# .wasm og .dll må serveres med riktig mimetype og brotli/gzip kompresjon hvis tilgjengelig
RUN printf '%s\n' \
    'server {' \
    '  listen 80;' \
    '  server_name _;' \
    '  root /usr/share/nginx/html;' \
    '  index index.html;' \
    '  location / { try_files $uri $uri/ /index.html =404; }' \
    '  location ~* \.(?:wasm)$ { types { application/wasm wasm; } }' \
    '  gzip on; gzip_types application/wasm application/octet-stream text/css application/javascript application/json;' \
    '}' > /etc/nginx/conf.d/default.conf

EXPOSE 80
HEALTHCHECK --interval=30s --timeout=3s CMD wget -qO- http://localhost/ >/dev/null || exit 1
