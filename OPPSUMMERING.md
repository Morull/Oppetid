# KraftverkUptime – prosjektoppsummering

Dato: april 2026
Status: Steg 0 ferdig, Steg 1 ferdig, Steg 2 gjenstår.

---

## Hva prosjektet er

Modulær oppetidsanalyse-plattform for vannkraftverk. Bygges trinnvis for å gå fra proxy-klassifisering basert på settlement-data, til full teknisk tilgjengelighet når SCADA og hydrologi kobles på. Designet for at hver modul skal kunne byttes ut uavhengig av de andre.

- **Første kunde:** Dalane-Kraft
- **Første anlegg:** Drivdal magasinkraftverk (~2,2 MW)
- **Planlagt utvidelse:** flere verk, elvekraftverk, SCADA, hydrologi, CMMS, brukertilgang
- **Stack:** .NET 10 + ASP.NET Core Minimal API, Blazor WebAssembly, PostgreSQL, Azure Container Apps, Entra ID

---

## Tre hovedprinsipper som styrer utviklingen

1. **Modul-nivå utskiftbarhet:** hver modul kan erstattes med en mer avansert versjon uten at andre moduler må endres. Valideres ved at en PR som oppgraderer én modul bare berører den modulens mappe pluss én linje i composition root.

2. **Additiv arkitektur:** utsatte valg (Event Hub, ADX, Redis, AKS) legges til *ved siden av* eksisterende kode, ikke erstatter den.

3. **Låse-nå vs utsette:** beslutninger som er dyre å endre senere (språk, frontend-rammeverk, datamodell, auth-sømmer) er låst i v1. Skaleringsvalg er utsatt til de faktisk trengs.

---

## Status per steg

### Steg 0 – Python proof-of-concept (FERDIG)

Gjennomført direkte på faktiske Drivdal-data fra februar 2025 (672 timer).

**Resultat:**
- Total produksjon: 703,55 MWh (matcher Summering-fanen eksakt)
- AvailabilityFactor: 62,65 %, CapacityFactor: 47,59 %
- PlanFulfillment: 93,98 %, BidAccuracy: 96,09 %
- Tilstandsfordeling: 370 InService, 163 ForcedOutage, 67 PlannedOutage, 51 ReserveShutdown, 21 ForcedDerating

**Leveranser:**
- `drivdal-feb2025-uptime-report.xlsx` – menneskelesbar rapport
- `drivdal-feb2025-fasit.json` – **regresjonsfasit** som .NET-implementasjonen skal matche
- `drivdal-feb2025-summary.txt` – tekstsammendrag
- `drivdal-analyse/` – full Python-implementasjon som referanse for .NET

### Steg 1 – Plattformskjelett i .NET (FERDIG)

Levert av annen chat mot `prompt-1-plattform.md`. Hele .NET-løsningen er bygget med alle kontrakter, retrofit-sømmer, infrastruktur, API, Blazor-shell, Worker, tester, Bicep og CI/CD.

**Leveranser i mappen:**
- `KraftverkUptime.sln` – Visual Studio solution
- `src/KraftverkUptime.Core/` – alle kontrakter (DataSources, Events, Jobs, Security, Storage, Reporting)
- `src/KraftverkUptime.Infrastructure/` – EF Core, Key Vault, Jobs, Storage, Telemetry
- `src/KraftverkUptime.Api/` – Minimal API med versjonering, policies, helse
- `src/KraftverkUptime.Web/` – Blazor WASM
- `src/KraftverkUptime.Worker/` – bakgrunnsjobb-host
- `src/KraftverkUptime.Modules.Settlement|Classification|Reporting/` – moduloppsett (implementasjoner kommer i Steg 2)
- `tests/` – Core- og Infrastructure-tester
- `infra/main.bicep` + moduler – Azure-infra
- `.github/workflows/ci.yml` + `deploy.yml` – CI/CD
- `docker-compose.yml`, `.env.example`
- `docs/architecture.md`, `docs/verify-step1.md`, `LEVERANSE-STEG1.md`

**Før du går til Steg 2:** følg `docs/verify-step1.md` og bekreft at `docker compose up` og `dotnet test` begge er grønne.

### Steg 2 – Domenemoduler (GJENSTÅR)

Neste leveranse. Kjøres i ny chat mot `prompt-2-domene.md`. Målet er:
- `SettlementDataSource` – leser portaleksport-Excel
- `UptimeAnalyzer.Settlement` – klassifisering og KPI-beregning
- `UptimeAnalyzer.Fused` – kontrakt for fremtidig SCADA-kobling (stub i v1)
- `Reporting.Uptime` – Excel-rapport med 3-veis figur

**Akseptansetest:** den nye .NET-implementasjonen kjører mot `drivdal-analyse/fixtures/drivdal-feb2025.xlsx` og produserer KPI-er som matcher `drivdal-feb2025-fasit.json`.

### Steg 3 og videre (FREMTID)

- Auth-modul (Entra ID, rollemodell med PlantReader/Analyst/Admin, OrgAdmin)
- Hydrologi-kobling (NVE Sildre API) – løfter klassifisering til Nivå 1
- SCADA-integrasjon (OPC UA / historian) – løfter til Nivå 3, reell teknisk tilgjengelighet per IEEE 762
- CMMS-integrasjon – løfter til Nivå 4
- Prediktivt vedlikehold – Nivå 5

---

## Hvordan mappen er organisert

```
C:\Morten\00 Oppetid\
│
├── OPPSUMMERING.md              ← Denne filen
├── FILOVERSIKT.md               ← Detaljert filforklaring
├── README.md                    ← Teknisk kom-i-gang-guide (Steg 1)
├── LEVERANSE-STEG1.md           ← Leveranserapport fra Steg 1-chat
├── NESTE-CHAT-START.md          ← Oppstartsmelding for ny chat
│
├── prompt-1-plattform.md        ← Spec som Steg 1 fulgte
├── prompt-2-domene.md           ← Spec som Steg 2 skal følge
├── prompt-oppetidsanalyse-kraftverk.md  ← Samlet referansedokument
│
├── drivdal-feb2025-uptime-report.xlsx   ← Rapport fra Steg 0
├── drivdal-feb2025-fasit.json           ← Regresjonsfasit for Steg 2
├── drivdal-feb2025-summary.txt          ← Tekstsammendrag
│
├── drivdal-analyse/             ← Python PoC (Steg 0)
│   ├── README.md
│   ├── fixtures/drivdal-feb2025.xlsx    ← Testfixtur for .NET
│   ├── src/                             ← Python-referanse for Steg 2
│   └── output/                          ← Duplikater av rapporter
│
├── KraftverkUptime.sln          ← .NET løsning (Steg 1)
├── Directory.Build.props
├── global.json                  ← .NET 10.0.100
├── docker-compose.yml           ← Lokal stack (Postgres + Azurite + app)
├── azure.yaml                   ← Azure Developer CLI
├── .env.example                 ← Eksempel-konfig
├── .editorconfig, .gitignore, .dockerignore
│
├── src/                         ← .NET kode
│   ├── KraftverkUptime.Core/
│   ├── KraftverkUptime.Infrastructure/
│   ├── KraftverkUptime.Api/
│   ├── KraftverkUptime.Web/
│   ├── KraftverkUptime.Worker/
│   ├── KraftverkUptime.Modules.Settlement/     ← Fylles i Steg 2
│   ├── KraftverkUptime.Modules.Classification/ ← Fylles i Steg 2
│   └── KraftverkUptime.Modules.Reporting/      ← Fylles i Steg 2
│
├── tests/                       ← Enhetstester
│   ├── KraftverkUptime.Core.Tests/
│   └── KraftverkUptime.Infrastructure.Tests/
│
├── infra/                       ← Bicep IaC
│   ├── main.bicep
│   ├── main.parameters.json
│   └── modules/ (containerAppsEnv, keyvault, postgres, storage, monitoring)
│
├── .github/workflows/           ← GitHub Actions
│   ├── ci.yml
│   └── deploy.yml
│
└── docs/
    ├── architecture.md
    └── verify-step1.md          ← Leses før Steg 2 startes
```

Totalt 153 filer.

---

## Hva du gjør nå

### Hvis du vil verifisere Steg 1 før du går videre

1. Åpne en terminal i `C:\Morten\00 Oppetid`.
2. `cp .env.example .env`
3. `docker compose up -d postgres` – starter lokal Postgres.
4. `dotnet ef migrations add Initial --project src/KraftverkUptime.Infrastructure --startup-project src/KraftverkUptime.Api`
5. `dotnet test` – skal være grønt.
6. `docker compose up` – hele stacken skal starte.
7. Åpne `http://localhost:5180` (Blazor) og `http://localhost:5080/health/ready` (API).

Full sjekkliste: `docs/verify-step1.md`.

### Hvis Steg 1 er verifisert og du vil gå videre

1. Start ny chat.
2. Lim inn innholdet i `NESTE-CHAT-START.md`.
3. Peg til at plattformen allerede står og at `prompt-2-domene.md` skal følges.
4. Den nye chatten bygger Settlement-, Classification- og Reporting-modulene.
5. Akseptansetest er at outputen matcher `drivdal-feb2025-fasit.json`.

---

## Viktige designbeslutninger som er låst

- **Språk:** .NET 10 (primær), Python kun for PoC/ML-jobb senere
- **Frontend:** Blazor WebAssembly (låst for å unngå retrofit)
- **Datamodell:** alle entiteter har `OwnerOrgId` og `PlantId` fra dag én (multi-tenancy forberedt)
- **Tid:** UTC internt, Europe/Oslo ved I/O, DST håndteres eksplisitt
- **Auth-sømmer:** `ICurrentUser`, `IQueryContext`, `IAuditLogger`, policy-navn (`PlantReader` etc.) eksisterer nå med no-op-implementasjoner – full Auth-modul legges til uten å endre andre moduler
- **Retrofit-sømmer:** `IJobQueue`, `IFileStorage`, `IDistributedCache`, `IPlantConfiguration`, `INotificationService`, `IEventPublisher` – alle med enkel v1-implementasjon som kan erstattes additivt
- **Identitetsleverandør:** Entra ID (låst)
- **Soft delete:** alle entiteter har `DeletedAt`/`DeletedBy`
- **API-versjonering:** `/api/v1/...` fra første endepunkt

---

## Fallgruver som allerede er flagget

- Tidssoner ved DST-overganger (23 og 25 timers døgn)
- Key Vault-throttling → caching obligatorisk
- Managed Identity lokalt → `DefaultAzureCredential` med Azure CLI-fallback
- `MWh=0 ≠ nedetid` før SCADA er koblet på (proxy-klassifisering er Nivå 0)
- Null-produksjon i magasinkraft er valg, i elvekraft er det hydrologi – klassifisering er betinget på `PlantType`

---

## Resultater som validerer tilnærmingen

Drivdal februar 2025, 672 timer:

| KPI | Verdi | Validering |
|---|---|---|
| Total produksjon | 703,55 MWh | Matcher Summering-fanen eksakt |
| Kapasitetsfaktor | 47,59 % | Innenfor benchmark 40–80 % for småkraft |
| PlanFulfillment | 93,98 % | Fornuftig for magasindrift |
| BidAccuracy | 96,09 % | God leveranse mot marked |
| AvailabilityFactor | 62,65 % | Proxy-verdi – faktisk høyere når SCADA kommer |

KPI-formlene følger IEEE 762 / NERC GADS. Metoden er derfor benchmarkbar mot bransjestatistikk.

---

## Hvem har gjort hva

Denne mappen er produsert over tre parallelle Claude-chatter:

- **Design-chatten (denne):** bygde prompt-bibliotek, leverte Python PoC, samlet filene.
- **Steg 1-chatten:** bygget hele .NET-plattformen mot prompt-1-plattform.md.
- **Steg 2-chatten (kommende):** bygger domenemoduler mot prompt-2-domene.md.

Prompt-bibliotek­tilnærmingen gjorde det mulig for tre chatter å jobbe uavhengig mot samme mappe og komme til samme resultat. Det er samme teknikk som beskrives i prompten under "Modul-nivå utskiftbarhet".
