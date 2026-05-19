# Neste sesjon — Prosjekt-rydding mai 2026

**Dato opprettet:** 2026-05-03
**Spec:** `docs/SPEC-CLEANUP-2026-05.md`
**Estimat:** 2-3 timer
**Bakgrunn:** Audit avdekket 27 .md i rot, død worktree, sikkerhets-CVE i pakke. Rydd og konsolider.

## Mål

Redusere prosjekt-roten fra 27 .md-filer til ~6 (`README.md`, `BACKLOG.md`, `JOURNAL.md`, siste 2 OVERLEVERING-er, prompt-*-onboarding). Alt annet i `docs/` eller `docs/archive/`.

I tillegg: bump OpenTelemetry for å lukke CVE, dokumenter dev-passord-håndtering.

## 7 faser i rekkefølge

| Fase | Innhold | Estimat |
|---|---|---|
| A | Slett død worktree + bump OpenTelemetry | 30 min |
| B | Slett ferdige NESTE-CHAT-filer + LEVERANSE-er | 15 min |
| C | Lag `docs/archive/`, flytt historiske OVERLEVERING-er | 15 min |
| D | Flytt referansemateriale (ANALYSE/ARKITEKTUR) til `docs/` | 15 min |
| E | Lag `BACKLOG.md` fra aktive NESTE-CHAT | 30 min |
| F | Lag `JOURNAL.md` fra OVERLEVERING-historikk | 30 min |
| G | Oppdater `README.md`, marker VEIKART avsluttet, dev-passord-doc | 30-45 min |

## Stoppunkter

- **Etter fase A:** kjør `dotnet build` + `dotnet test`. Stopp og rapporter resultat. Hvis testene faller, debug før neste fase.
- **Etter fase G (siste):** kjør full smoke-test mot appen, alle KPI-sider. Stopp og rapporter før commit.

## Bekreftede design-valg

| Valg | Beslutning |
|---|---|
| Ferdige NESTE-CHAT | Slettes (har korresponderende OVERLEVERING) |
| Aktive NESTE-CHAT | Flyttes til `docs/archive/specs-active/` og refereres fra BACKLOG |
| Gamle OVERLEVERING-er | Flyttes til `docs/archive/overleveringer/`, behold de 2 nyeste i rot |
| OPPSUMMERING/FILOVERSIKT/NESTE-CHAT-START | Flyttes til `docs/archive/` (ikke slett — historikk) |
| ANALYSE-*/ARKITEKTUR-* | Flyttes til `docs/` (referansemateriale) |
| OpenTelemetry | Bump til 1.15.3, fjern NU1902 fra NoWarn |
| Dev-passord | Bytt til `dev-only-replace-in-prod` (oppdater docker-compose også) |
| `prompt-*.md` (3 stk) | Behold i rot — onboarding-spec for nye sesjoner |
| `drivdal-analyse/` | Behold — regresjons-fasit |

## Spørre-policy

- Hvis en "ferdig" NESTE-CHAT viser seg å ha unik info som ikke er i OVERLEVERING: flytt til arkiv istedenfor å slette
- Hvis OpenTelemetry 1.15.3 ikke bygger pga API-endring: rapporter og pause før neste fase
- Hvis Postgres-container ikke starter etter passord-bytte: dokumenter problem og rull tilbake (men behold bytte i `appsettings.json`)
- Hvis testene faller etter fase A: stopp øyeblikkelig — kan indikere at OpenTelemetry-bumpen krever kode-endring

## Forventet sluttilstand

```
C:\Morten\00 Oppetid\
├── README.md                                       ← Oppdatert
├── BACKLOG.md                                      ← NY
├── JOURNAL.md                                      ← NY
├── OVERLEVERING-2026-05-03-MVP-HARDENING.md        ← Beholdes (siste)
├── OVERLEVERING-2026-05-03-IMPORT-COMPLETENESS.md  ← Beholdes (nest siste)
├── prompt-1-plattform.md                           ← Onboarding
├── prompt-2-domene.md                              ← Onboarding
├── prompt-oppetidsanalyse-kraftverk.md             ← Onboarding
├── docs/
│   ├── SPEC-*.md                                   ← Aktive specs
│   ├── ANALYSE-VIRKNINGSGRAD.md                    ← Flyttet hit
│   ├── ANALYSE-NEDETID-SCADA.md                    ← Flyttet hit
│   ├── ARKITEKTUR-SCADA.md                         ← Flyttet hit
│   ├── SCADA_eksport_referanse.md                  ← Flyttet hit
│   ├── architecture.md
│   ├── VEIKART-AUTONOM.md                          ← Markert AVSLUTTET
│   └── archive/
│       ├── overleveringer/                         ← Alle gamle OVERLEVERING-er
│       ├── specs-active/                           ← Aktive NESTE-CHAT
│       ├── OPPSUMMERING.md                         ← Foreldet
│       ├── FILOVERSIKT.md                          ← Foreldet
│       └── NESTE-CHAT-START.md                     ← Foreldet
├── src/
├── tests/
└── drivdal-analyse/
```

## Verifikasjon

Etter alle faser, kjør:

```powershell
cd C:\Morten\00 Oppetid

# Filer i rot (skal være ~6-8)
Get-ChildItem -Filter "*.md" | Measure-Object | Select-Object -ExpandProperty Count

# Ingen worktrees
git worktree list

# Ingen sårbarheter
dotnet list package --vulnerable --include-transitive

# Tester grønne
dotnet test

# App starter og rendrer
.\"Start Oppetid.bat"
```

## Når ferdig

Skriv `OVERLEVERING-2026-MM-DD-CLEANUP.md` med:
- Antall filer slettet (Fase B), flyttet (C/D/G), opprettet (E/F)
- Test-resultat før og etter
- Vulnerability-scan-resultat før og etter
- Skjermbilde av prosjekt-rot (Get-ChildItem)
- Eventuelle filer bevart utenfor scope, med begrunnelse

Foreslåtte oppfølginger:
- Implementer AUTO-IMPORT-FOLDER (toppen av BACKLOG)
- Implementer Lindland-mapping (multi-generator)
- Honnefoss + Liavatn etter at SCADA-data er bekreftet
