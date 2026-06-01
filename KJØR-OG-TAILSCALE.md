# Kjøre KraftverkUptime + dele via Tailscale

Oppsett for alltid-på server med én ren HTTPS-URL for de som skal logge på.

## Hva som er endret (for flerbruker-tilgang)

Blazor-frontenden hadde hardkodet `ApiBaseAddress: http://localhost:5080`.
Siden WASM kjører i brukerens nettleser betydde det at appen kun virket på
selve serveren. Endringene under gjør at API-kall går til *samme adresse* som
siden serveres fra, slik at den virker likt fra localhost, LAN og Tailscale:

- `wwwroot/appsettings.json` — fjernet `ApiBaseAddress` (faller tilbake til samme origin).
- `caddy/Caddyfile` — Caddy serverer `/` → web og `/api`,`/swagger`,`/health` → api.
- `docker-compose.tailscale.yml` — legger til Caddy på `127.0.0.1:8080`.
- `docker-compose.yml` — postgres/azurite/api/web bindes til `127.0.0.1` (kun Tailscale eksponerer appen).
- `.env` — opprettet med sterkt Postgres-passord.

Alt er reversibelt via git (`git diff`, `git checkout -- <fil>`).

## Steg 1 — Start stacken

Docker Desktop må kjøre. I prosjektmappa:

```powershell
docker compose -f docker-compose.yml -f docker-compose.tailscale.yml up --build -d
```

Første build tar noen minutter. DB-skjema (inkl. SCADA-tabeller) opprettes
automatisk ved oppstart.

## Steg 2 — Verifiser lokalt på serveren

Åpne i nettleser på selve server-PC-en:

- App: http://localhost:8080
- API/Swagger: http://localhost:8080/swagger
- Helse: http://localhost:8080/health/ready

Sjekk containere: `docker compose ps` (alle skal være `running`/`healthy`).

> Merk: databasen starter **tom** — tidligere importerte data lå i Docker-volumer
> som ikke følger med når du bare flytter prosjektmappa. Last opp CSV på nytt via
> appen, eller legg filer i hot-folder-mappa `CSV Eksporter`.

## Steg 3 — Tailscale

Engangsoppsett i Tailscale-admin (https://login.tailscale.com/admin/dns):
slå på **MagicDNS** og **HTTPS Certificates**.

På server-PC-en (Tailscale må være pålogget):

```powershell
tailscale serve --bg localhost:8080
```

Dette gir HTTPS på `https://<maskinnavn>.<tailnet>.ts.net` → localhost:8080,
med automatisk gyldig sertifikat. Sjekk status:

```powershell
tailscale serve status
```

Skru av igjen ved behov: `tailscale serve --https=443 off`

## Steg 4 — Gi tilgang til de som skal logge på

Inviter dem til tailnettet (Tailscale-admin → Users → Invite). De installerer
Tailscale-appen, logger inn, og åpner **én lenke**:

```
https://<maskinnavn>.<tailnet>.ts.net
```

Ingen portnummer, ingen sertifikatadvarsel, kun synlig på tailnettet ditt.

> Sikkerhet: API-et er anonymt (ingen innlogging) i v1. Med dette oppsettet er
> det kun nåbart for de du har gitt tailnet-tilgang. Ikke bruk `tailscale funnel`
> (offentlig internett) før du har autentisering på plass.

## Auto-start

`restart: unless-stopped` er allerede satt på alle containere, så de starter med
Docker Desktop. Sørg for at Docker Desktop og Tailscale starter med Windows
(begge har «Start on login» i innstillingene). `tailscale serve` er persistent
(lagres i tailnet-konfigen).

## Feilsøking

| Symptom | Løsning |
|---|---|
| `docker compose` henger på "postgres starting" | Gi det opptil 30s. Ellers `docker compose logs postgres`. |
| 502 fra Caddy på /api | API ikke oppe enda. `docker compose logs api`. Caddy prøver på nytt automatisk. |
| App laster, men data/kall feiler | Tøm nettleser-cache (bootstrap-filer er no-cache, men gjør hard refresh). |
| Tailscale-URL gir sertifikatfeil | MagicDNS + HTTPS Certificates må være på i admin. |
| Port 8080 opptatt | Endre venstre side i `docker-compose.tailscale.yml` og `tailscale serve`-porten. |

## Rull tilbake endringene

```powershell
git checkout -- docker-compose.yml src/KraftverkUptime.Web/wwwroot/appsettings.json
# og slett caddy/Caddyfile + docker-compose.tailscale.yml hvis ønskelig
```
