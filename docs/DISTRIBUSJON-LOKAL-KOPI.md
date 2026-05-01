# Distribusjon — lokal kopi til kollega

**Bruksområde:** Du vil dele en fungerende kopi (kode + data) av KraftverkUptime med en kollega slik at de kan kjøre systemet lokalt på sin egen PC. Hver kjører sin egen versjon — ingen synk mellom dere etter overføring.

**Forutsetninger på mottaker-PC:**
- Windows 10/11 med admin-rettigheter
- Docker Desktop installert og kjørende ([https://www.docker.com/products/docker-desktop/](https://www.docker.com/products/docker-desktop/))
- ~10 GB ledig diskplass
- PowerShell (følger med Windows)

---

## Del 1 — Eksporter pakke (du som sender)

### Steg 1.1: Stopp eventuelle pågående endringer

Sørg for at du ikke har en pågående opplasting eller endring i UI-en. Vent til alle bakgrunnsjobber er ferdige (sjekk `/reports`-siden — alle skal stå som "Klar").

### Steg 1.2: Kjør eksport-scriptet

Åpne PowerShell og naviger til prosjektmappen:

```powershell
cd "C:\Morten\00 Oppetid"
```

Kjør distribusjons-scriptet (lager 3 filer):

```powershell
# 1. Eksporter Postgres-database
docker compose exec postgres pg_dump -U kraftverk -d kraftverk > backup-postgres.sql

# 2. Eksporter Azurite-blobber (settlement-rapporter, SCADA-CSV-er)
docker run --rm -v 00oppetid_azurite-data:/data -v "${PWD}:/backup" alpine sh -c "tar czf /backup/backup-azurite.tar.gz -C /data ."

# 3. Verifiser at begge filene er laget
Get-ChildItem backup-*.* | Select-Object Name, @{n='Size (MB)';e={[math]::Round($_.Length/1MB,1)}}
```

Forventet output: `backup-postgres.sql` (typisk 1-50 MB) og `backup-azurite.tar.gz` (typisk 5-100 MB).

### Steg 1.3: Rydd build-output før zipping

Build-output og volum-mapper kan gjøre zip-en unødvendig stor:

```powershell
# Vis hva som vil bli ryddet (dry-run)
git clean -xdfn -e backup-postgres.sql -e backup-azurite.tar.gz

# Hvis det ser greit ut, fjern -n for å faktisk rydde:
git clean -xdf -e backup-postgres.sql -e backup-azurite.tar.gz
```

`-e` betyr "ekskluder denne filen fra opprydding" — backup-filene skal IKKE slettes.

### Steg 1.4: Lag zip-pakken

```powershell
# Lag zip én mappe opp så vi unngår å zippe seg selv
Compress-Archive -Path * -DestinationPath ..\KraftverkUptime-distribusjon.zip -Force

# Verifiser
Get-Item ..\KraftverkUptime-distribusjon.zip | Select-Object Name, @{n='Size (MB)';e={[math]::Round($_.Length/1MB,1)}}
```

Forventet: en zip på ~50-200 MB.

### Steg 1.5: Send zip-en til kollegaen

OneDrive, e-post (hvis liten), eller delt nettverksmappe — alle fungerer for selve filen. Bare ikke be kollegaen kjøre fra OneDrive — de må pakke ut til en lokal mappe.

---

## Del 2 — Importer på mottaker-PC

### Steg 2.1: Pakk ut zip-en

Mottaker pakker ut til en lokal mappe, f.eks.:

```
C:\KraftverkUptime\
```

**IKKE** under OneDrive, Dropbox eller annen fil-synk-mappe — det vil korrumpere Docker-volumene.

### Steg 2.2: Start Docker-stacken

Åpne PowerShell og naviger til mappen:

```powershell
cd C:\KraftverkUptime
docker compose up -d --build
```

Første gang tar bygging 5-10 min. Vent til alle 5 containere er "Up" og "healthy":

```powershell
docker compose ps
```

Forventet output:

```
NAME                   STATUS
00oppetid-api-1        Up (healthy)
00oppetid-azurite-1    Up
00oppetid-postgres-1   Up (healthy)
00oppetid-web-1        Up
00oppetid-worker-1     Up
```

### Steg 2.3: Gjenopprett databasen

```powershell
# Vent 10 sekunder for at Postgres skal være helt klar
Start-Sleep -Seconds 10

# Gjenopprett dump
Get-Content backup-postgres.sql | docker compose exec -T postgres psql -U kraftverk -d kraftverk
```

Forvent at det skrolles forbi en del NOTICE-meldinger om eksisterende tabeller — det er fordi Docker-stacken auto-oppretter skjemaet ved oppstart, og dump-en fyller inn data over.

Hvis du vil ha en helt ren tilstand før import:

```powershell
# Slett databasen helt og start blankt (advarsel: sletter all data)
docker compose down -v
docker compose up -d
Start-Sleep -Seconds 15
Get-Content backup-postgres.sql | docker compose exec -T postgres psql -U kraftverk -d kraftverk
```

### Steg 2.4: Gjenopprett Azurite-blobber

```powershell
docker run --rm -v 00oppetid_azurite-data:/data -v "${PWD}:/backup" alpine sh -c "cd /data && tar xzf /backup/backup-azurite.tar.gz"
```

### Steg 2.5: Restart API + Worker så de plukker opp gjenoppstartet data

```powershell
docker compose restart api worker web
```

### Steg 2.6: Verifiser at alt virker

Åpne i nettleser: **http://localhost:5180**

Sjekk:
- `/plants` — viser alle 11 anlegg
- `/portefolje` — viser KPI-tabell
- `/nedetid` — kan velge anlegg og periode
- `/vakt-roi` — viser ROI-tall

Eller test API direkte:

```powershell
curl.exe "http://localhost:5080/api/v1/plants" | ConvertFrom-Json | Select-Object -ExpandProperty items
```

Forventet: 11 anlegg listet.

---

## Daglig bruk på mottaker-PC

**Start-stack hver morgen** (hvis containerne er stoppet):

```powershell
cd C:\KraftverkUptime
docker compose up -d
```

**Stopp** (når ferdig for dagen, valgfritt — Docker kan også kjøre i bakgrunnen):

```powershell
cd C:\KraftverkUptime
docker compose stop
```

**Last opp ny settlement-fil**:

Drag-and-drop i UI: gå til http://localhost:5180/upload — velg anlegg og fil.

---

## Feilsøking

### "docker compose: command not found"

Sjekk at Docker Desktop er installert og kjører. Test:

```powershell
docker --version
docker compose version
```

### "Cannot connect to the Docker daemon"

Docker Desktop er installert men ikke startet. Start Docker Desktop fra Start-menyen og vent på at den er klar (whale-ikonet i system tray skal være stille, ikke roterende).

### Container "00oppetid-postgres-1" går ned umiddelbart

Sjekk loggen:

```powershell
docker compose logs postgres --tail 50
```

Vanligste årsak: port 5432 er allerede i bruk på maskinen (annen Postgres kjører). Stopp den andre, eller endre port i `docker-compose.yml`:

```yaml
postgres:
  ports:
    - "5433:5432"   # endret fra 5432:5432
```

### "psql: ERROR:  duplicate key value violates unique constraint"

Datasen er ikke tom før gjenopprett. Bruk full reset:

```powershell
docker compose down -v
docker compose up -d
Start-Sleep -Seconds 15
Get-Content backup-postgres.sql | docker compose exec -T postgres psql -U kraftverk -d kraftverk
```

### Web-siden viser tom side

Hard-refresh i nettleser: **Ctrl+F5**. Hvis fortsatt tomt, sjekk at WASM laster:

```powershell
docker compose logs web --tail 30
```

### Kollegaen ser andre tall enn deg

Forventet — etter overføring kjører dere hver vår egen versjon. Endringer hos én synes ikke hos den andre. Hvis dere må holde seg synkronisert, gjenta hele eksport-/import-prosessen periodisk.

---

## Begrensninger ved denne distribusjons-modellen

| Begrensning | Konsekvens |
|---|---|
| Egne, separate databaser | Endringer ikke synlige mellom PC-er |
| Manuell synk | Re-eksport hver gang dere vil ha samme tall |
| Ingen brukertilganger | Alle har full admin på sin kopi |
| Ingen sentral backup | Hver må selv backe opp sin egen kopi |
| Vanskelig å diskutere konkrete tall | Versjonene divergerer over tid |

For permanent flerbruker-bruk: sett opp på en intern server — se `docs/SPEC-PROD-DEPLOY.md` (kommer senere).

---

## Vedlegg — Automatiserings-script

Hvis du gjør dette ofte, lag `lag-distribusjon.ps1` i prosjektroten:

```powershell
# lag-distribusjon.ps1
param(
    [string]$Output = "..\KraftverkUptime-distribusjon.zip"
)

$ErrorActionPreference = "Stop"

Write-Host "1/5 Eksporterer Postgres..." -ForegroundColor Cyan
docker compose exec postgres pg_dump -U kraftverk -d kraftverk > backup-postgres.sql

Write-Host "2/5 Eksporterer Azurite..." -ForegroundColor Cyan
docker run --rm -v 00oppetid_azurite-data:/data -v "${PWD}:/backup" alpine sh -c "tar czf /backup/backup-azurite.tar.gz -C /data ."

Write-Host "3/5 Rydder build-output..." -ForegroundColor Cyan
git clean -xdf -e backup-postgres.sql -e backup-azurite.tar.gz | Out-Null

Write-Host "4/5 Lager zip..." -ForegroundColor Cyan
if (Test-Path $Output) { Remove-Item $Output }
Compress-Archive -Path * -DestinationPath $Output

Write-Host "5/5 Ferdig!" -ForegroundColor Green
Get-Item $Output | Select-Object Name, @{n='Size (MB)';e={[math]::Round($_.Length/1MB,1)}}
```

Da kan du kjøre:

```powershell
.\lag-distribusjon.ps1
```

Og få en ferdig zip i én kommando.

Tilsvarende `installer-distribusjon.ps1` for mottakeren:

```powershell
# installer-distribusjon.ps1 — kjøres i utpakket mappe
$ErrorActionPreference = "Stop"

Write-Host "1/4 Bygger og starter containere..." -ForegroundColor Cyan
docker compose up -d --build
Start-Sleep -Seconds 15

Write-Host "2/4 Gjenoppretter database..." -ForegroundColor Cyan
Get-Content backup-postgres.sql | docker compose exec -T postgres psql -U kraftverk -d kraftverk

Write-Host "3/4 Gjenoppretter blobber..." -ForegroundColor Cyan
docker run --rm -v 00oppetid_azurite-data:/data -v "${PWD}:/backup" alpine sh -c "cd /data && tar xzf /backup/backup-azurite.tar.gz"

Write-Host "4/4 Restarter tjenester..." -ForegroundColor Cyan
docker compose restart api worker web

Write-Host "Ferdig! Åpne http://localhost:5180 i nettleser." -ForegroundColor Green
```

Inkluder begge scriptene i zip-pakken så slipper kollegaen å kopiere og lime inn kommandoer.
