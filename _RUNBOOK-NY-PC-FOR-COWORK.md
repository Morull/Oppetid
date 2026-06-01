# Runbook for Cowork — flytte appen til ny PC

**Til Cowork (Claude):** Når brukeren har pakket ut zip-en til `C:\Morten\00 Oppetid` på den nye PC-en og linket mappa, gå gjennom denne sjekklista trinn for trinn. Ikke ta snarveier — bekreft hvert steg før du går videre. Bruker har klargjort backupene; din jobb er å restore + konfigurere uten å miste data.

---

## Fase 1 — Verifiser miljøet før du gjør noe

- [ ] **Bekreft at mappa er der.** `Glob` på `C:\Morten\00 Oppetid\docker-compose.yml`. Skal finnes.
- [ ] **Bekreft at backup-filene er med.** `Glob` på `C:\Morten\00 Oppetid\_volume-backup\*.tar.gz`. Skal finne to filer:
  - `postgres-data.tar.gz` (~180 MB)
  - `azurite-data.tar.gz` (~70 MB)
  Hvis de mangler — STOPP og spør bruker. Uten dem mister vi dataene.
- [ ] **Bekreft at Docker Desktop kjører.** Be bruker om å åpne PowerShell og kjøre `docker ps`. Hvis kommandoen feiler eller henger: Docker Desktop er ikke startet. Bruker må starte den og vente til den viser «Engine running».
- [ ] **Bekreft at det ikke ligger gamle containere/volumer fra et tidligere forsøk.** `docker volume ls | findstr 00oppetid`. Hvis 00oppetid_postgres-data eller 00oppetid_azurite-data allerede finnes — spør bruker om de skal slettes (tomme) før restore. Skal være tomt på ny PC.

---

## Fase 2 — Restore Docker-volumene

Bruker må kjøre disse kommandoene i PowerShell. Du kan ikke kjøre docker-kommandoer direkte mot Windows fra sandboxen — bruk **computer-use** mot PowerShell, eller bare gi kommandoene til bruker for kopier/lim.

- [ ] **Gå til prosjektmappa:** `cd "C:\Morten\00 Oppetid"`
- [ ] **Stopp eventuelle containere (ufarlig hvis ingen kjører):** `docker compose down`
- [ ] **Opprett de to navngitte volumene:**
  ```powershell
  docker volume create 00oppetid_postgres-data
  docker volume create 00oppetid_azurite-data
  ```
- [ ] **Restore postgres-data:**
  ```powershell
  docker run --rm -v 00oppetid_postgres-data:/v -v "${PWD}\_volume-backup:/b" alpine sh -c "cd /v && tar xzf /b/postgres-data.tar.gz"
  ```
  Skal returnere uten output. Hvis feilmelding — STOPP.
- [ ] **Restore azurite-data:**
  ```powershell
  docker run --rm -v 00oppetid_azurite-data:/v -v "${PWD}\_volume-backup:/b" alpine sh -c "cd /v && tar xzf /b/azurite-data.tar.gz"
  ```
- [ ] **Verifiser at volumene har innhold:**
  ```powershell
  docker run --rm -v 00oppetid_postgres-data:/v alpine ls /v | findstr PG_VERSION
  ```
  Skal liste `PG_VERSION` (Postgres data-fil). Hvis tom — restore feilet.

---

## Fase 3 — Konfigurer ApiBaseAddress

Web-appen trenger å peke mot riktig host. Default `localhost:5080` fungerer kun for nettleser på server-PC-en selv.

- [ ] **Spør bruker:** «Hvilken adresse skal andre PC-er bruke for å nå appen? Tre alternativer:
   1. **Tailscale-navn** (anbefalt, men krever at Tailscale er installert på server + klienter) — f.eks. `oppetid-server`
   2. **LAN-IP** for lokalnett-bruk — finn via `ipconfig` på server-PC-en (IPv4-adresse, typisk `192.168.x.y`)
   3. **Bare localhost** hvis du kun skal bruke server-PC-en til å åpne appen»

- [ ] **Read** `C:\Morten\00 Oppetid\src\KraftverkUptime.Web\wwwroot\appsettings.json`. Sjekk nåværende verdi.

- [ ] **Edit** filen og bytt `"ApiBaseAddress": "http://localhost:5080/"` til den nye verdien:
   - For Tailscale: `"ApiBaseAddress": "http://oppetid-server:5080/"`
   - For LAN-IP: `"ApiBaseAddress": "http://192.168.x.y:5080/"`
   - For localhost-only: la den stå

---

## Fase 4 — Bygg og start

- [ ] **Bygg web på nytt** (appsettings ble endret, må bakes inn i imaget):
  ```powershell
  docker compose build web
  ```
  Tar 1-3 minutter første gang. Hvis bruker har endret andre filer kan du gjøre `docker compose build` på hele løsningen.

- [ ] **Start opp:**
  ```powershell
  docker compose up -d
  ```

- [ ] **Sjekk at alle 5 containere er oppe og friske:**
  ```powershell
  docker compose ps
  ```
  Skal vise `00oppetid-postgres-1`, `azurite-1`, `api-1`, `worker-1`, `web-1` — alle med status `running` (postgres med `healthy`).

- [ ] **Vent 30-60 sekunder** for at API skal kjøre migrasjoner og worker skal koble seg på.

- [ ] **Sjekk API-logger for migrasjons-meldinger:**
  ```powershell
  docker compose logs api --tail 50
  ```
  Forventet: melding om at migrasjoner kjørte uten feil. Skjema-broene i `DatabaseBootstrapper` skal være idempotente — ingenting skal bli ødelagt selv om alt allerede er på plass.

---

## Fase 5 — Verifiser at appen og dataene er der

- [ ] **Test localhost-tilgang.** Bruk Chrome MCP til å navigere til `http://localhost:5180/portefolje`. Forventet: Portefølje-Sammendrag-fanen viser tall fra restored data.
- [ ] **Sjekk anleggslista.** Skal være 11 anlegg (Drivdal, Grødemfoss, Haukland, Honnefoss, Liavatn, Lindland, Løgjen, Øgreyfoss, Ørsdalen, Stølskraft, Vikeså). Hvis færre — restore er ufullstendig.
- [ ] **Sjekk et konkret tall mot tidligere kjente verdier:** Sammendrag-fanen for «Hittil i år (2026)» skal vise Spotomsetning ≈ **88,5 mill NOK** (det jeg testet på gamle PC-en før flytting). Hvis tallet er vesentlig annerledes — undersøk hvorfor.
- [ ] **Test åpning av Rapport-detalj.** Klikk Haukland → Rapport-detalj. Skal vise Drift / Marked / Økonomi-seksjoner og KAIA-kostnad-kort.

---

## Fase 6 — Brannmur og ekstern tilgang

Dette krever Windows-admin-rettigheter, så bruker må gjøre selv. Ikke prøv via computer-use uten å ha sjekket først.

- [ ] **Forklar bruker:** for at andre PC-er på lokalnett skal nå appen må Windows Defender Firewall åpne portene 5180 (web) og 5080 (API) for innkommende trafikk på private nettverk. PowerShell som admin:
  ```powershell
  New-NetFirewallRule -DisplayName "Oppetid Web 5180" -Direction Inbound -LocalPort 5180 -Protocol TCP -Action Allow -Profile Private
  New-NetFirewallRule -DisplayName "Oppetid API 5080" -Direction Inbound -LocalPort 5080 -Protocol TCP -Action Allow -Profile Private
  ```
- [ ] **Hvis bruker valgte Tailscale**: brannmurregelen ovenfor er nok — Tailscale-trafikken kommer inn på private nettverks-profilen.
- [ ] **Test fra en annen enhet på samme nett / tailnet.** Bruker bekrefter visuelt at appen åpner.

---

## Fase 7 — Avslutning og dokumentasjon

- [ ] **Bekreft for bruker at flyttingen er fullført.**
- [ ] **Spør om backup-mappa kan slettes** for å spare plass: `Remove-Item -Recurse -Force C:\Morten\00 Oppetid\_volume-backup`. Anbefal å vente noen dager til alt er kjørt inn først.
- [ ] **Mind brukeren om Docker Desktop sin Windows-sesjons-bivirkning:** for at appen skal kjøre 24/7 må Windows ha auto-innlogging (eller bruker logger inn) og Docker Desktop må starte ved innlogging. Slå av dvale i `Strøm og dvale → Aldri`.
- [ ] **Oppdater masterplanen** (`MASTERPLAN-CODE-2026-05-22.md`) hvis nødvendig med en kort statusnotis om at flyttingen er fullført.

---

## Hvis noe feiler

- **`docker compose build` feiler med nettverksfeil:** Docker Desktop trenger internett for å hente base-images. Sjekk internett-tilkobling.
- **`postgres-1` starter ikke / går i restart-loop:** sannsynlig versjons-mismatch (selv om compose pinner `postgres:16-alpine`). Sjekk loggen `docker compose logs postgres`. Hvis det er en data-format-feil (sjelden), restore-en kan være korrupt — start på nytt på gamle PC og verifiser at containerne var stoppet før backup.
- **Web åpner men «Kunne ikke hente»-feil overalt:** ApiBaseAddress peker på feil host. Sjekk filen i `wwwroot/appsettings.json` og rebuild web.
- **Andre PC-er får «kan ikke nå serveren»:** brannmur ikke åpnet, eller (hvis Tailscale) Tailscale ikke kjører på server.

---

## Notater for ny sesjon

Når du leser denne fila for første gang i ny Cowork-sesjon på ny PC: spør bruker om de allerede har gjort noen av stegene over (f.eks. installert Docker Desktop, satt opp Tailscale). Ikke gjenta steg de allerede har gjort. Be om at de viser deg `docker ps`-output og `docker volume ls` for å forstå statusen først.
