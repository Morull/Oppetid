# Spec: Prosjekt-rydding mai 2026

**Status:** Klar til eksekvering (2026-05-03)
**Estimat:** 2-3 timer
**Bakgrunn:** Audit 2026-05-03 avdekket 27 .md-filer i prosjekt-roten med blanding av aktiv styring, historikk, referansemateriale og utdaterte planer. En død worktree, sikkerhets-CVE i pakke, og spec-filer som ikke reflekterer at funksjonene er implementert.

## Mål

Redusere prosjekt-roten fra 27 .md-filer til 3 styringsfiler (`README.md`, `BACKLOG.md`, `JOURNAL.md`) + de 1-2 nyeste OVERLEVERING-ene som fortsatt er fersk arbeidshukommelse. Alt annet flyttes til `docs/` eller `docs/archive/`.

I tillegg: bump OpenTelemetry-pakke for å lukke kjent CVE, og dokumenter dev-passord-overstyring i README før prod-deploy.

## Faser

Eksekveres i rekkefølge. Hver fase har eget PowerShell-script og verifikasjon.

| Fase | Innhold | Estimat |
|---|---|---|
| A | Slett død worktree + bump OpenTelemetry | 30 min |
| B | Slett ferdige NESTE-CHAT-filer + LEVERANSE-er | 15 min |
| C | Lag `docs/archive/`, flytt historiske OVERLEVERING-er | 15 min |
| D | Flytt referansemateriale (ANALYSE/ARKITEKTUR) til `docs/` | 15 min |
| E | Lag `BACKLOG.md` fra aktive NESTE-CHAT | 30 min |
| F | Lag `JOURNAL.md` fra OVERLEVERING-historikk | 30 min |
| G | Oppdater `README.md`, `OPPSUMMERING.md`, `FILOVERSIKT.md`, `VEIKART-AUTONOM.md` | 30-45 min |

**Stoppunkt:** etter fase A og B, kjør `dotnet build` og `dotnet test` for å bekrefte at ingenting er ødelagt. Etter fase G, åpne appen og verifiser at alle KPI-sider fortsatt rendrer.

## Fase A: Worktree + sikkerhetsfix

### A1. Slett død worktree

Worktree `.claude/worktrees/beautiful-elbakyan-dd576d/` er allerede merget tilbake til main per `OVERLEVERING-2026-05-03-MVP-HARDENING.md`. Inneholder kun foreldede kopier + bin/obj som tar titalls MB.

```powershell
cd C:\Morten\00 Oppetid

# Verifiser status først
git worktree list

# Slett worktree
git worktree remove .claude/worktrees/beautiful-elbakyan-dd576d --force

# Hvis worktree ikke kjenner til den (manuelt opprettet): manuell sletting
if (Test-Path ".claude\worktrees\beautiful-elbakyan-dd576d") {
    Remove-Item -Recurse -Force ".claude\worktrees\beautiful-elbakyan-dd576d"
}

# Slett tom worktrees-mappe hvis det er den eneste
$worktreeDir = ".claude\worktrees"
if ((Test-Path $worktreeDir) -and ((Get-ChildItem $worktreeDir).Count -eq 0)) {
    Remove-Item $worktreeDir
}

git worktree prune
```

### A2. Bump OpenTelemetry

CVE GHSA-g94r-2vxg-569j i OpenTelemetry 1.12.0 (notert i `OVERLEVERING-2026-04-26.md`).

```powershell
cd C:\Morten\00 Oppetid
# Oppdater pakke
dotnet add src\KraftverkUptime.Api package OpenTelemetry --version 1.15.3
dotnet add src\KraftverkUptime.Api package OpenTelemetry.Extensions.Hosting --version 1.15.3
dotnet add src\KraftverkUptime.Api package OpenTelemetry.Instrumentation.AspNetCore --version 1.15.3
dotnet add src\KraftverkUptime.Api package OpenTelemetry.Instrumentation.Http --version 1.15.3
# Sjekk om Worker har samme avhengighet
dotnet add src\KraftverkUptime.Worker package OpenTelemetry --version 1.15.3
```

Etter bump: fjern `<NoWarn>NU1902</NoWarn>` fra `Directory.Build.props` eller fra prosjektfilene hvis det ble lagt til som workaround.

### A3. Verifikasjon fase A

```powershell
dotnet restore
dotnet build
dotnet test
# Forventet: 290/290 grønne, 0 warnings
```

## Fase B: Slett ferdige NESTE-CHAT + LEVERANSE-er

### B1. Verifiser hva som er ferdig

Disse filene har korresponderende `OVERLEVERING-*-<NAVN>.md` eller commit-historikk som viser at de er konsumert:

| Fil | Bevis for at den er ferdig |
|---|---|
| `NESTE-CHAT-MVP-HARDENING.md` | `OVERLEVERING-2026-05-03-MVP-HARDENING.md` |
| `NESTE-CHAT-IMPORT-COMPLETENESS.md` | `OVERLEVERING-2026-05-03-IMPORT-COMPLETENESS.md` |
| `NESTE-CHAT-PRODUKSJON-FIX.md` | `KONTROLLSJEKK-PRODUKSJON-FIX.md` (commit `1f07db9`) |
| `NESTE-CHAT-CAPTURE-RATE.md` | Implementert per commit-historikk |
| `NESTE-CHAT-KASKADE-DAMMER.md` | Implementert per commit-historikk |
| `NESTE-CHAT-HYDROGRID-API.md` | Implementert per commit-historikk |
| `NESTE-CHAT-HYDROGRID-PORTEFOLJE.md` | Implementert per commit-historikk |
| `KONTROLLSJEKK-PRODUKSJON-FIX.md` | Engangs review-prompt, jobben er gjort |
| `LEVERANSE-STEG1.md` | Steg 1 ferdig per VEIKART |
| `LEVERANSE-STEG2.md` | Steg 2 ferdig per VEIKART |

### B2. Slett

```powershell
cd C:\Morten\00 Oppetid

$filerSomSlettes = @(
    "NESTE-CHAT-MVP-HARDENING.md",
    "NESTE-CHAT-IMPORT-COMPLETENESS.md",
    "NESTE-CHAT-PRODUKSJON-FIX.md",
    "NESTE-CHAT-CAPTURE-RATE.md",
    "NESTE-CHAT-KASKADE-DAMMER.md",
    "NESTE-CHAT-HYDROGRID-API.md",
    "NESTE-CHAT-HYDROGRID-PORTEFOLJE.md",
    "KONTROLLSJEKK-PRODUKSJON-FIX.md",
    "LEVERANSE-STEG1.md",
    "LEVERANSE-STEG2.md"
)

foreach ($fil in $filerSomSlettes) {
    if (Test-Path $fil) {
        Remove-Item $fil
        Write-Host "Slettet: $fil"
    } else {
        Write-Host "Eksisterer ikke (allerede slettet?): $fil"
    }
}
```

**Stopp og bekreft** med bruker hvis noe av de "ferdige" filene faktisk inneholder informasjon som ikke er overført til OVERLEVERING. Hvis usikker: flytt til `docs/archive/` istedenfor å slette.

## Fase C: Lag docs/archive/ + flytt historiske OVERLEVERING-er

### C1. Opprett arkiv-struktur

```powershell
cd C:\Morten\00 Oppetid

New-Item -ItemType Directory -Force -Path "docs\archive\overleveringer"
New-Item -ItemType Directory -Force -Path "docs\archive\specs-implemented"
```

### C2. Flytt OVERLEVERING-er eldre enn de 2 nyeste

```powershell
# List alle OVERLEVERING-er i rot, sortert nyest først
$overleveringer = Get-ChildItem -Path . -Filter "OVERLEVERING-*.md" |
    Sort-Object LastWriteTime -Descending

# Behold de 2 nyeste i rot (fersk arbeidshukommelse)
# Resten flyttes til docs/archive/overleveringer/
$gamleOverleveringer = $overleveringer | Select-Object -Skip 2

foreach ($fil in $gamleOverleveringer) {
    Move-Item $fil.FullName "docs\archive\overleveringer\$($fil.Name)"
    Write-Host "Flyttet: $($fil.Name)"
}
```

## Fase D: Flytt referansemateriale til docs/

ANALYSE-* og ARKITEKTUR-* er referansedokumenter, ikke aktiv styring. De skal i `docs/`.

```powershell
cd C:\Morten\00 Oppetid

$referansefiler = @(
    "ANALYSE-VIRKNINGSGRAD.md",
    "ANALYSE-NEDETID-SCADA.md",
    "ARKITEKTUR-SCADA.md",
    "SCADA_eksport_referanse.md"
)

foreach ($fil in $referansefiler) {
    if (Test-Path $fil) {
        Move-Item $fil "docs\$fil"
        Write-Host "Flyttet: $fil → docs\"
    }
}
```

**Etter flytting:** søk i `docs/`-filer og `src/` etter referanser til disse filene. Oppdater stier til ny lokasjon hvis nødvendig.

## Fase E: Lag BACKLOG.md

Aktive NESTE-CHAT samles til én backlog-fil. Format:

```markdown
# Backlog — KraftverkUptime

**Sist oppdatert:** 2026-05-03

Aktive specs som ikke er implementert ennå. Sortert etter prioritet.

## Høy prioritet

### 1. Auto-import fra overvåket mappe
- **Spec:** `docs/SPEC-AUTO-IMPORT-FOLDER.md`
- **Estimat:** 2-3 dager
- **Kickstart:** `docs/archive/specs-active/NESTE-CHAT-AUTO-IMPORT-FOLDER.md`
- **Verdi:** Eliminerer ~80 % av manuell import-administrasjon. Bygger på import-completeness.

### 2. Lindland SCADA-mapping + multi-generator
- **Spec:** `docs/SPEC-LINDLAND-MAPPING.md`
- **Estimat:** 1 dag
- **Kickstart:** `docs/archive/specs-active/NESTE-CHAT-LINDLAND-MAPPING.md`
- **Avhenger av:** Kaskade-dammer (ferdig)
- **Verdi:** Lindland operativ med G1+G2, ~32 MNOK/år porteføljeverdi

### 3. Honnefoss SCADA-mapping
- **Spec:** `docs/SPEC-HONNEFOSS-MAPPING.md`
- **Estimat:** 4-6 timer
- **Kickstart:** `docs/archive/specs-active/NESTE-CHAT-HONNEFOSS-MAPPING.md`
- **Krever bruker-input:** topology-bekreftelse for KYDLNDVT/INNTAK + at REVSVT/NODLANDVT er flyttet til Liavatn-kraftverk

### 4. Liavatn-kraftverk SCADA-mapping
- **Spec:** `docs/SPEC-LIAVATN-MAPPING.md`
- **Estimat:** 3-4 timer
- **Krever bruker-input:** dedikert SCADA-eksport for Liavatn-kraftverket (mottatt skjermbilde, ikke CSV ennå)

## Medium prioritet

(Tomt — flytt punkter hit fra Høy etterhvert som de blir mindre presserende)

## Lav prioritet / ut-av-scope-v1

- Cloud-basert hot folder (Azure Blob trigger)
- ML-modell på Hydrogrid-prognose-pålitelighet
- Vannverdi-modell (alternativkost for vakt-ROI)
- TimescaleDB-bytte når sample-volum vokser
- Multi-tenant
```

**Eksekvering:**

```powershell
cd C:\Morten\00 Oppetid

# Flytt aktive NESTE-CHAT til arkiv (referert fra BACKLOG)
New-Item -ItemType Directory -Force -Path "docs\archive\specs-active"

$aktiveNesteChat = @(
    "NESTE-CHAT-AUTO-IMPORT-FOLDER.md",
    "NESTE-CHAT-LINDLAND-MAPPING.md",
    "NESTE-CHAT-HONNEFOSS-MAPPING.md"
)

foreach ($fil in $aktiveNesteChat) {
    if (Test-Path $fil) {
        Move-Item $fil "docs\archive\specs-active\$fil"
    }
}

# BACKLOG.md skrives manuelt med innholdet over
# (eller Claude Code genererer den ved å lese spec-headerne)
```

## Fase F: Lag JOURNAL.md

Kronologisk sammendrag av OVERLEVERING-er. 5-10 linjer per dato. Format:

```markdown
# Journal — KraftverkUptime utviklingshistorikk

Kort kronologisk sammendrag. Full detalj i `docs/archive/overleveringer/`.

## 2026-05-03 — MVP-hardening + Import-completeness levert

- Sikkerhets-audit fjernet AllowAnonymous fra alle write-endepunkter, Entra ID-roller satt opp
- Drivdal-regresjonstest reaktivert med fasit for feb-2026
- DataQualityQueryService + portefølje-kolonne + per-anlegg-widget
- PlantType-bevisst klassifikator (RunOfRiver, RegulatedHydro, Mixed, PumpedStorage)
- `core.data_completeness_digests` + ukentlig digest-job + arkiv-side
- 290/290 tester grønne. Detaljer: `docs/archive/overleveringer/OVERLEVERING-2026-05-03-MVP-HARDENING.md` + `...-IMPORT-COMPLETENESS.md`

## 2026-04-30 — Spec-dag

- Sju nye specs skrevet: CAPTURE-RATE, KASKADE-DAMMER, PRODUKSJON-FIX, HYDROGRID-API, HYDROGRID-PORTEFOLJE, MVP-HARDENING, IMPORT-COMPLETENESS
- Honnefoss og Liavatn mapping startet basert på SCADA-skjermbilder
- Audit av Produksjon-modul (Drivdal feb-2026) — KPI-tall korrekte men UI-tooltips misvisende

## 2026-04-29 — Veikart Steg 0–7 ferdig

- Multi-anleggs-import + auto-opprett 11 plants
- PUT /plants/{id}/admin + admin-side
- Effektivitets-modul + algoritme-basert sweet-spot
- ScadaClassifier 9-state + FusionClassifier
- Event-KPI-er: MTBF, MTTR, FOR, EAF
- Portefølje-side + GET /portfolio/kpis
- OperlogCsvParser anlegg-uavhengig

## 2026-04-28 — SCADA-arkitektur og drag-drop

- ARKITEKTUR-SCADA spec
- Drag-drop SCADA-import
- Drivdal SCADA-data importert

## 2026-04-26 — Sikkerhetsfunn OpenTelemetry CVE notert

## 2026-04-23 — Fullstendig vakt-ROI med overløp + ubalanse

## 2026-04-21 — .NET 10 migrering

## (eldre — se docs/archive/overleveringer/)
```

**Eksekvering:** lag fila manuelt eller la Claude Code generere ved å lese de arkiverte OVERLEVERING-headerne.

## Fase G: Oppdater utdaterte filer

### G1. README.md (root)

Skriv ny `README.md` som speiler dagens faktiske tilstand:

```markdown
# KraftverkUptime

Drifts- og KPI-platform for Dalane Krafts 11 vannkraftverk i Sokndal.

## Status (2026-05-03)

- **11 anlegg** i porteføljen
- **290/290 tester grønne**, 0 build-warnings
- Aktive moduler: Nedetid, Vakt-ROI, Capture rate, Effektivitet, Produksjon, Portefølje, Hydrogrid-diagnostikk, Datakvalitet, Import-status
- Auth: Entra ID med Authenticated/Admin-roller
- Datakilder: settlement (KAIA), SCADA, operlog, Hydrogrid-plan

## Kom i gang

```powershell
.\"Start Oppetid.bat"
# Eller manuelt:
docker-compose up -d  # Postgres + Azurite
dotnet run --project src\KraftverkUptime.Api
dotnet run --project src\KraftverkUptime.Web
```

API: http://localhost:5080
Web: http://localhost:5180

## Arkitektur

.NET 10 modulær monolitt — se `docs/architecture.md`.

## Aktiv utvikling

- Pågående arbeid: `BACKLOG.md`
- Historikk: `JOURNAL.md`
- Utviklings-veikart: `docs/VEIKART-AUTONOM.md` (steg 0-7 avsluttet 2026-04-29)

## Produksjons-deploy

`appsettings.json` har dev-defaults (Postgres-passord, Azurite-key). 
**Før prod-deploy:** overstyr alle credentials via `appsettings.Production.json` 
eller env-variabler. Aldri commit reelle credentials.
```

### G2. Slett eller oppdater foreldede filer

```powershell
cd C:\Morten\00 Oppetid

# Disse er erstattet av README + BACKLOG + JOURNAL
$foreldedeFiler = @(
    "OPPSUMMERING.md",       # Sa "Steg 2 gjenstår" — alt ferdig nå
    "FILOVERSIKT.md",        # Refererte python-PoC, ikke dagens .NET-løsning
    "NESTE-CHAT-START.md"    # Refererte .NET 8, prosjektet er .NET 10
)

# Flytt til arkiv (ikke slett — kan ha historisk verdi)
foreach ($fil in $foreldedeFiler) {
    if (Test-Path $fil) {
        Move-Item $fil "docs\archive\$fil"
    }
}
```

### G3. Marker VEIKART som avsluttet

`docs/VEIKART-AUTONOM.md` — legg til på toppen:

```markdown
> **Status: AVSLUTTET 2026-04-29.** Alle 8 steg implementert og levert. 
> Bevares som referanse for autonom arbeidsflyt-mønster i fremtidige sesjoner. 
> Aktiv backlog: `BACKLOG.md`.
```

### G4. Dokumenter dev-passord-håndtering

I `appsettings.json`: vurder å bytte default Postgres-passord fra `kraftverk` til `dev-only-replace-in-prod`:

```json
{
  "Database": {
    "ConnectionString": "Host=localhost;Port=5432;Database=kraftverkuptime;Username=kraftverk;Password=dev-only-replace-in-prod"
  }
}
```

Krever oppdatering av `docker-compose.yml` slik at Postgres-container starter med samme passord. README oppdateres med tydelig instruksjon om at prod krever overstyring.

## Akseptansekriterier

### Filer

1. Worktree `.claude/worktrees/beautiful-elbakyan-dd576d/` finnes ikke lenger
2. `git worktree list` viser kun main
3. Prosjekt-roten har max 6 .md-filer:
   - `README.md`
   - `BACKLOG.md`
   - `JOURNAL.md`
   - `OVERLEVERING-2026-05-03-MVP-HARDENING.md` (siste)
   - `OVERLEVERING-2026-05-03-IMPORT-COMPLETENESS.md` (nest siste)
   - `prompt-*.md` (3 onboarding-filer beholdes)
4. `docs/` inneholder ANALYSE-*, ARKITEKTUR-SCADA.md, SCADA_eksport_referanse.md
5. `docs/archive/overleveringer/` inneholder alle OVERLEVERING-er fra april 2026 og tidligere
6. `docs/archive/specs-active/` inneholder NESTE-CHAT for AUTO-IMPORT, LINDLAND, HONNEFOSS
7. `docs/archive/` inneholder de gamle OPPSUMMERING/FILOVERSIKT/NESTE-CHAT-START

### Kode

8. `dotnet build` grønt
9. `dotnet test` 290/290 grønne (eller flere hvis nye tester ble lagt til)
10. `dotnet list package --vulnerable` returnerer 0 sårbarheter (etter OpenTelemetry-bump)
11. `Directory.Build.props` har ikke `NU1902` i NoWarn lenger

### Dokumentasjon

12. `README.md` reflekterer faktisk dagens status (11 anlegg, 290 tester, alle moduler)
13. `BACKLOG.md` har 4 aktive specs med riktig prioritet og estimater
14. `JOURNAL.md` har minst 5 datoer med 5-10-linjers sammendrag
15. `docs/VEIKART-AUTONOM.md` har "AVSLUTTET 2026-04-29"-banner

### Smoke-test

16. Åpne appen, naviger til alle hovedsider — ingen feil
17. Drag-drop test-fil — fortsatt fungerer
18. Auth-flow fungerer (logg inn, sjekk at admin-endepunkter krever rolle)

## Implementasjons-rekkefølge

| Steg | Fase | Stoppunkt |
|---|---|---|
| 1 | A — Slett worktree, bump OpenTelemetry | **Stopp og rapporter** dotnet test-resultat |
| 2 | B — Slett ferdige NESTE-CHAT + LEVERANSE | – |
| 3 | C — Lag docs/archive, flytt historiske OVERLEVERING | – |
| 4 | D — Flytt referansedok til docs/ | – |
| 5 | E — Lag BACKLOG.md | – |
| 6 | F — Lag JOURNAL.md | – |
| 7 | G — Oppdater README, marker VEIKART avsluttet, dev-passord-doc | – |
| 8 | Verifikasjon — full build, test, smoke-test | **Stopp og rapporter** før commit |

## Antakelser

1. **Git-historikken bevares** — alle slettinger gjøres med `git rm` eller `Remove-Item`, så filene kan gjenfinnes via `git log` hvis nødvendig
2. **Ingen aktiv pull request** mot disse filene — sjekk `git status` før eksekvering
3. **OpenTelemetry 1.15.3 er bakover-kompatibel** med 1.12.0-API — ingen kode-endringer trengs (verifiser med build)
4. **Postgres-passord-bytte krever container-rebuild** — dokumenter i README

## Ut-av-scope

- Refaktorering av kode (kun fil-flyt og dokumentasjon)
- Ny funksjonalitet
- Migrering fra in-memory jobbkø til Service Bus
- Multi-tenant-utvidelser
- Implementasjon av aktive specs (AUTO-IMPORT, LINDLAND, HONNEFOSS, LIAVATN) — disse forblir på BACKLOG

## Verifikasjon

```powershell
# Etter alle faser:
cd C:\Morten\00 Oppetid

# 1. Filstruktur i rot
Get-ChildItem -Filter "*.md" | Select-Object Name | Format-Table
# Forventet: maks 6 filer

# 2. Worktree er borte
git worktree list
# Forventet: kun main

# 3. Sårbarheter
dotnet list package --vulnerable --include-transitive
# Forventet: ingen treff for OpenTelemetry

# 4. Tester
dotnet test
# Forventet: 290/290 grønne (eller flere)

# 5. App starter
.\"Start Oppetid.bat"
# Åpne http://localhost:5180 — sjekk at alt rendrer
```

## Når ferdig

Skriv `OVERLEVERING-2026-MM-DD-CLEANUP.md` med:
- Antall filer slettet/flyttet/opprettet
- Bekreftelse at tester fortsatt grønne (290+)
- Bekreftelse at vulnerabilities = 0
- Skjermbilde av ny prosjekt-rot (rent og organisert)
- Eventuelle filer som ble bevart utenfor scope (med begrunnelse)
