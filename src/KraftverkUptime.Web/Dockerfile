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

# .wasm og .dll må serveres med riktig mimetype og brotli/gzip kompresjon hvis tilgjengelig.
# Cache-strategi (Testrapport 2026-05-21 funn #2 — feilen vedvarte etter rebuild fordi
# browseren hadde cached bootstrap-filene):
#   Bootstrap-filer (index.html, blazor.webassembly.js, dotnet.js, dotnet.runtime.js,
#   dotnet.native.js) har IKKE hash-navn og blir aggressivt cached av browseren.
#   En rebuild med ny kode plukkes da IKKE opp uten manuell cache-clear.
#   Løsning: no-cache på bootstrap-filene; hash-fingerprintede assets kan
#   fortsatt caches lenge (filnavnet endres ved hver innholds-endring).
RUN printf '%s\n' \
    'server {' \
    '  listen 80;' \
    '  server_name _;' \
    '  root /usr/share/nginx/html;' \
    '  index index.html;' \
    '  # Bootstrap-filer: tving revalidering mot serveren ved hver navigasjon.' \
    '  location = /index.html { add_header Cache-Control "no-cache, no-store, must-revalidate"; expires 0; }' \
    '  location = /_framework/blazor.webassembly.js { add_header Cache-Control "no-cache"; expires 0; }' \
    '  location = /_framework/dotnet.js { add_header Cache-Control "no-cache"; expires 0; }' \
    '  # Hash-fingerprintede assets (filnavn inneholder content-hash) kan caches lenge.' \
    '  location ~* "^/_framework/.+\.[a-z0-9]{10}\.(wasm|js)(\.(br|gz))?$" { add_header Cache-Control "public, max-age=31536000, immutable"; }' \
    '  location / { try_files $uri $uri/ /index.html =404; }' \
    '  location ~* \.(?:wasm)$ { types { application/wasm wasm; } }' \
    '  gzip on; gzip_types application/wasm application/octet-stream text/css application/javascript application/json;' \
    '}' > /etc/nginx/conf.d/default.conf

EXPOSE 80
HEALTHCHECK --interval=30s --timeout=3s CMD wget -qO- http://localhost/ >/dev/null || exit 1
