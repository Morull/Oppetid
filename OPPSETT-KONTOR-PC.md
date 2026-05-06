# Oppsett: kjør Oppetid på en kontor-PC for hele teamet

Mål: flytte Oppetid-stacken fra utvikler-PC-en til en dedikert kontor-PC,
slik at flere kan bruke den samtidig over kontornettverket. Ingen kode-
endringer trengs — Docker er allerede konfigurert for nettverks-tilgang.

**Estimat:** 1-2 timer for første gangs oppsett. Vedlikehold etterpå:
~5 minutter per kode-oppdatering.

---

## Forutsetninger

På den ekstra kontor-PC-en:
- Windows 10/11 (64-bit) eller Windows Server 2019+
- Minimum 8 GB RAM (16 GB anbefalt — postgres + .NET API + WASM-bygg)
- 50 GB ledig disk (Docker images + postgres-volum + blob-storage)
- Lokal admin-tilgang (for å installere Docker + åpne brannmur-port)
- Tilkoblet kontornettverket med kabel eller wifi

På din egen utvikler-PC: prosjektet er allerede på plass.

---

## Steg 1: Klargjør kontor-PC-en (15 min)

### 1.1 Installer Docker Desktop

1. Gå til https://www.docker.com/products/docker-desktop og last ned installeren
2. Kjør installeren — velg "Use WSL 2 instead of Hyper-V" hvis du får valget
3. Restart PC-en når installeren ber om det
4. Start Docker Desktop fra Start-menyen
5. Vent til hval-ikonet i systray er stabilt grønt — første gang tar 1-2 minutter
6. (Valgfritt) Innstillinger → General → kryss av "Start Docker Desktop when you log in"

### 1.2 Slå på "Run Docker Desktop without admin"
Innstillinger → General → "Use the WSL 2 based engine" må være på.
Innstillinger → Resources → WSL Integration → kryss på Ubuntu hvis du har det.

### 1.3 Sjekk at Docker fungerer
Åpne PowerShell og kjør:
```powershell
docker --version
docker run hello-world
```
Skal skrive "Hello from Docker!" — da er du klar.

---

## Steg 2: Få koden over på kontor-PC-en (10 min)

Du har to alternativer:

### Alternativ A: Git (anbefalt — gjør oppdatering enkelt senere)

```powershell
cd C:\
git clone <repo-url> Oppetid
cd Oppetid
```

Hvis du ikke har repo-URL, hopp til alternativ B.

### Alternativ B: Kopier filer manuelt

1. På utvikler-PC-en: zip hele mappa `C:\Morten\00 Oppetid` (uten `.git`-mappa
   hvis du vil spare plass)
2. Kopier til kontor-PC-en (USB-stick / nettverk / OneDrive)
3. Pakk ut til `C:\Oppetid` (eller annet sted uten æøå i stien)

---

## Steg 3: Konfigurer for nettverkstilgang (5 min)

### 3.1 Lag en `.env`-fil i prosjekt-rota

Lag fil `.env` i samme mappe som `docker-compose.yml`:

```env
# Web-grensesnittet — port 5180 er default. Endre hvis port er opptatt.
WEB_PORT=5180

# API — kun for ekstern integrasjon. Vanlige brukere trenger ikke denne.
API_PORT=5080

# Postgres — kun for direktedump/backup. La default stå hvis ikke i bruk.
POSTGRES_PORT=5432

# Hot-folder for SCADA/settlement-eksporter. På kontor-PC-en bør dette være
# en mappe på en delt nettverks-drive slik at andre kan dropbe filer der
# uten å måtte ha tilgang til selve PC-en. Eksempel:
HOT_FOLDER_HOST_PATH=Z:\Oppetid\CSV Eksporter

# Hvis du vil ha lokal mappe på selve PC-en:
# HOT_FOLDER_HOST_PATH=C:\Oppetid\CSV Eksporter
```

> **NB om hot-folderen:** Hvis du legger den på en mappet nettverks-drive
> (`Z:\`), må Docker Desktop ha tilgang til den drive-en. Innstillinger →
> Resources → File Sharing → legg til drive-bokstaven hvis Docker klager
> på "mount denied".

### 3.2 Sjekk Windows-brannmuren

Default på Windows 10/11 er at innkommende på "Public network" er blokkert.
PowerShell som **administrator**:

```powershell
# Tillat innkommende på 5180 fra hele LAN-en
New-NetFirewallRule -DisplayName "Oppetid Web (5180)" `
    -Direction Inbound -Protocol TCP -LocalPort 5180 -Action Allow

# Hvis du også vil eksponere API:
New-NetFirewallRule -DisplayName "Oppetid API (5080)" `
    -Direction Inbound -Protocol TCP -LocalPort 5080 -Action Allow
```

Verifiser at PC-en er på "Private/Domain network" (ikke "Public"):
**Innstillinger → Nettverk → Wifi/Ethernet → Network profile → Private**.

---

## Steg 4: Start app-en (10 min første gang)

```powershell
cd C:\Oppetid
.\Start Oppetid.bat
```

Første kjøring bygger Docker-imager — tar 3-8 minutter. Etterpå starter alt.

Verifiser at det er oppe:
```powershell
docker compose ps
# Alle skal være "Up" eller "healthy"
```

Test fra kontor-PC-en selv: åpne `http://localhost:5180` i nettleseren.

---

## Steg 5: Finn IP-adressen og test fra en annen PC (5 min)

På kontor-PC-en, PowerShell:

```powershell
ipconfig | findstr IPv4
```

Notér IPv4-adressen (eks. `192.168.1.42` eller `10.0.0.15`).

På en kollega sin PC: åpne nettleser, gå til `http://192.168.1.42:5180`
(bytt ut med din PC sin IP). Det skal funke umiddelbart.

### Tips: gi kontor-PC-en et navn

Hvis IT har satt opp DNS internt, kan du nå PC-en på maskin-navnet:
```
http://oppetid-server:5180
```

Sjekk maskin-navnet med `hostname` i PowerShell.

---

## Steg 6: Få app-en til å starte automatisk (10 min)

Du vil ikke logge inn på PC-en hver gang den restarter. Tre nivåer av
"automatisk":

### 6.1 Docker starter alltid (allerede ordnet)
Docker Desktop → Innstillinger → "Start Docker Desktop when you log in".

### 6.2 Containere restarter ved Docker-restart
Allerede konfigurert i `docker-compose.yml` (`restart: unless-stopped`).
Verifiser:
```powershell
docker compose ps --format "{{.Name}}: {{.Status}}"
```
Hvis det IKKE står "restart: unless-stopped" på dine containere,
legg til denne linjen under hver tjeneste i `docker-compose.yml`:
```yaml
restart: unless-stopped
```

### 6.3 Auto-login på Windows (slipper å ha en bruker logget inn)

Hvis kontor-PC-en alltid skal stå på og kjøre Oppetid uten at noen
trykker login: aktiver auto-login med `netplwiz`:

1. Trykk Win+R, skriv `netplwiz`, trykk Enter
2. Velg brukeren som skal logges inn
3. Fjern haken på "Users must enter a user name and password"
4. Skriv inn passord når Windows spør

> **Sikkerhetsnotat:** Dette betyr at den fysiske PC-en er åpen for
> alle som kommer til tastaturet. Sett en låst skjerm-saver hvis PC-en
> står tilgjengelig.

---

## Steg 7: Backup av data (10 min)

Postgres-volumet ligger inne i Docker. Hvis kontor-PC-en krasjer eller
disken dør, mister du alle imports + annoteringer + cause-aliaser.

### 7.1 Daglig automatisk backup (anbefalt)

Lag fila `Backup-database.ps1` i prosjekt-rota:

```powershell
$ErrorActionPreference = "Stop"
$backupDir = "C:\Oppetid-backup"
New-Item -ItemType Directory -Force -Path $backupDir | Out-Null

$timestamp = Get-Date -Format "yyyyMMdd_HHmm"
$file = "$backupDir\kraftverk_$timestamp.sql"

docker exec 00oppetid-postgres-1 pg_dump -U kraftverk kraftverk > $file
Write-Host "Backup OK: $file"

# Behold siste 14 dager, slett resten
Get-ChildItem $backupDir -Filter "kraftverk_*.sql" |
    Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-14) } |
    Remove-Item
```

Sett opp Task Scheduler til å kjøre den daglig kl. 02:00:
1. `Win+R` → `taskschd.msc`
2. Create Basic Task → "Oppetid backup"
3. Trigger: Daily 02:00
4. Action: Start a program → `powershell.exe`
5. Arguments: `-ExecutionPolicy Bypass -File "C:\Oppetid\Backup-database.ps1"`

### 7.2 Backup av blob-storage (azurite)

Azurite-volumet inneholder rapport-blob-er (UptimeReport JSON). Mindre
kritisk fordi de kan re-genereres fra settlement-imports, men kjekt å ha:

```powershell
# Som del av backup-scriptet
$blobBackup = "$backupDir\azurite_$timestamp.tar"
docker run --rm -v 00oppetid_azurite-data:/data -v ${backupDir}:/backup `
    alpine tar -cf /backup/azurite_$timestamp.tar /data
```

---

## Steg 8: Hot-folder for kollegaer (15 min)

Drifts-personell trenger å droppe SCADA/settlement-eksporter inn i en
mappe som hot-folder-watcheren plukker opp. På en delt kontor-PC bør
dette være en delt nettverks-mappe.

### 8.1 Opprett en delt mappe

På kontor-PC-en, PowerShell som admin:

```powershell
$dir = "C:\Oppetid-imports"
New-Item -ItemType Directory -Force -Path $dir | Out-Null

# Del mappa over nettverket
New-SmbShare -Name "OppetidImports" -Path $dir -ChangeAccess "Everyone"
```

### 8.2 Pek docker-compose mot delt mappe

I `.env`:
```env
HOT_FOLDER_HOST_PATH=C:\Oppetid-imports
```

Restart stacken:
```powershell
.\Stopp Oppetid.bat
.\Start Oppetid.bat
```

### 8.3 Kollegaer får tilgang

På kollega sin PC:
```
\\oppetid-server\OppetidImports
```
(bytt `oppetid-server` med kontor-PC-ens hostname eller IP)

De kan nå dra-og-slippe SCADA-eksporter direkte hit. Hot-folder-watcheren
ser filene innen sekunder og auto-importerer.

---

## Vedlikehold

### Daglig sjekk (1 min)

```powershell
docker compose ps
```

Hvis noe er "Restarting" eller "Exited", kjør:
```powershell
.\Stopp Oppetid.bat
.\Start Oppetid.bat
```

### Oppdater til ny versjon

Hvis du brukte git i steg 2:
```powershell
cd C:\Oppetid
git pull
.\Start Oppetid.bat   # bygger om automatisk
```

Hvis du kopierte manuelt: zip + kopier på nytt → `Start Oppetid.bat`.
Postgres-volumet beholder seg, så data og annoteringer er trygge.

### Sjekk logger ved feil

```powershell
docker logs 00oppetid-api-1 --tail 50
docker logs 00oppetid-web-1 --tail 50
docker logs 00oppetid-worker-1 --tail 50
```

---

## Sikkerhets-status

**Akkurat nå:** Alle på kontor-LAN-et kan se ALLE data. Auth er anonym
(stub). Det er OK i et internt bedriftsnett, men ikke OK hvis:
- Du har gjester på samme wifi
- VPN-en til kontoret er åpen for ikke-ansatte
- Du eksponerer porten over internett

**Når dere vil ha auth:** se `docs/SPEC-MULTI-USER-DEPLOYMENT.md` for
Entra ID-integrasjon. Det er en 1-2 dagers jobb når dere kommer dit.

---

## Hjelp / feilsøking

### "Cannot connect to the Docker daemon"
Docker Desktop kjører ikke. Start det fra Start-menyen og vent 1-2 min.

### "Port 5180 is already in use"
En annen app bruker porten. Endre `WEB_PORT=5181` i `.env` og start på nytt.

### Sider laster, men "Behandles" på alle imports
Worker-containeren kjører ikke eller har feil. Sjekk `docker logs 00oppetid-worker-1`.
Vanlig årsak: postgres var ikke klar da worker startet. `Stopp + Start` fikser
det vanligvis.

### Kollegaer kan ikke nå PC-en på IP
1. Sjekk at PC-en er på "Private network" (ikke "Public")
2. Sjekk brannmur-regelen i steg 3.2
3. Test fra kontor-PC-en selv først: `http://localhost:5180` skal funke
4. `Test-NetConnection oppetid-server -Port 5180` fra kollega-PC

---

Spør hvis du står fast på et steg. Det er nok flere ting på Windows som
oppfører seg ikke akkurat som beskrevet — fortell hvilket steg + feil-
melding så hjelper jeg.
