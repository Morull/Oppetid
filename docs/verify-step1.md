# Verifikasjon – Steg 1 (plattformskjelett)

`dotnet test` og `docker compose up` skal være grønt før Steg 2 startes. Fordi denne sesjonen ble generert uten tilgang til `dotnet` eller `docker` i sandkassen, må følgende kjøres på din egen maskin.

## 1. Forutsetninger

- Docker Desktop, Podman eller OrbStack
- .NET 10 SDK (`global.json` låser versjonen)
- `dotnet-ef` CLI:
  ```sh
  dotnet tool install --global dotnet-ef
  ```

## 2. Klon og åpne mappen

Alle filer i denne leveransen ligger i `outputs/`. Kopier dem til et git-repo på maskinen din (eller åpne mappen direkte), så:

```sh
cd <repo>
cp .env.example .env
```

## 3. Generer første EF Core-migrering

Dette må gjøres én gang etter at prosjektet er klonet. Plattformen har `EnsureCreated`-fallback i dev hvis du hopper over steget, men for en ordentlig oppsett gjør:

```sh
docker compose up -d postgres
dotnet ef migrations add Initial \
    --project src/KraftverkUptime.Infrastructure \
    --startup-project src/KraftverkUptime.Api \
    --output-dir Persistence/Migrations \
    --context KraftverkDbContext
```

Commit de genererte filene i `src/KraftverkUptime.Infrastructure/Persistence/Migrations/`.

## 4. Kjør tester

```sh
dotnet restore KraftverkUptime.sln
dotnet build KraftverkUptime.sln --configuration Release --no-restore
dotnet test KraftverkUptime.sln --configuration Release --no-build
```

Forventet: alle tester i `KraftverkUptime.Core.Tests` og `KraftverkUptime.Infrastructure.Tests` er grønne.

## 5. Kjør alt lokalt

```sh
docker compose up --build
```

Etter at alle containere er "healthy":

| URL | Skal svare |
| --- | --- |
| http://localhost:5080/health/live | 200, JSON `{"status":"Healthy",...}` |
| http://localhost:5080/health/ready | 200 etter at Postgres er klar |
| http://localhost:5080/swagger | Swagger-UI lastes |
| http://localhost:5180 | Blazor WASM: forside "Oversikt" |
| http://localhost:5180/plants | Tom tabell (ingen anlegg registrert i v1) |

## 6. Rask røyktest

```sh
curl -s http://localhost:5080/health/live | jq
curl -s "http://localhost:5080/api/v1/plants?pageSize=10" | jq
```

`plants`-endepunktet returnerer `{ "items": [], "nextCursor": null, "pageSize": 10 }` når databasen er tom. Det er forventet — anleggsregistrering kommer i Prompt 2.

## 7. Når alt er grønt

Commit eventuelle EF-migrerings-filer. Deretter er du klar for Prompt 2:

```sh
git add .
git commit -m "Steg 1: KraftverkUptime plattformskjelett (.NET 10)"
```

## Kjente fallgruver hvis noe feiler

1. **`docker compose up` henger på postgres-health.** Gi det 30s. Hvis ikke: `docker compose logs postgres`.
2. **`dotnet test` feiler med "EFCore.NamingConventions not found".** Kjør `dotnet restore` på nytt.
3. **API svarer 500 på oppstart med "pending migrations".** Første migrering mangler — gå til steg 3.
4. **`dotnet ef` kommando feiler med "Unable to locate DbContext".** Sjekk at Postgres faktisk kjører (`docker compose up -d postgres`) og at connection string i `appsettings.Development.json` peker på `localhost:5432`.
5. **CORS-feil i Blazor WASM når den kaller API.** Sjekk at `ASPNETCORE_ENVIRONMENT=Development` er satt på API-containeren. Policy åpner kun i dev-miljø.
