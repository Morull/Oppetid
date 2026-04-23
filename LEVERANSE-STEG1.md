# Leveranse – Steg 1: Plattformskjelett

Dato: 2026-04-20
Status: **ferdig**

## Hva er gjort

Bygget et komplett plattformskjelett for KraftverkUptime i .NET 10. Ingen domenelogikk – kun kontrakter, sømmer og infrastruktur. Domenemoduler (settlement-parser, KPI, klassifisering) kommer i Steg 2.

**133 filer** fordelt på:

- 8 .NET-prosjekter (Core, Infrastructure, Api, Web, Worker + 3 modul-placeholdere)
- 2 testprosjekter (Core + Infrastructure)
- Docker-oppsett for lokal kjøring (Postgres, Azurite, Api, Worker, Web)
- Bicep for full Azure-provisjonering via `azd up`
- GitHub Actions for CI (build, test, secret scan, SBOM) og deploy (OIDC)

## Beslutninger du tok underveis

| Tema | Valg | Grunn |
| --- | --- | --- |
| Runtime | .NET 10 (ikke 8) | Støtte til november 2028 istedenfor november 2026 |
| Meldingsflyt | Egenskrevet dispatcher, ikke MediatR | Unngår kommersiell lisensusikkerhet |
| Kontraktsjusteringer | 7 småforbedringer godtatt | Tettere kontrakter, mindre teknisk gjeld |

De syv justeringene: eksplisitt `ReportRequest`, `AssetEvent.Value` splittes i DB-lag, `IJobHandler<T>` lagt til, `IPlantConfiguration` gjort async, `CancellationToken` i `IAuditLogger`, `HasAllPlantsAccess`-flagg på `ICurrentUser`, `IEventPublisher` som eneste fasade for eventer.

## Hvor er hva

| Fil/mappe | Innhold |
| --- | --- |
| `README.md` | Full teknisk dokumentasjon – **start her** |
| `prompt-1-plattform.md` | Oppdatert spesifikasjon som reflekterer det som faktisk er bygget |
| `docs/architecture.md` | Fulle C4-diagrammer (Context, Container, Component, dataflyt) |
| `docs/verify-step1.md` | Nøyaktig kommandoliste for å få `dotnet test` og `docker compose up` grønt |
| `src/KraftverkUptime.Core/` | Alle kontrakter og domenetyper – **hjertet av løsningen** |
| `src/KraftverkUptime.Infrastructure/` | Alt det tekniske: EF Core, jobbkø, lagring, telemetri, Key Vault |
| `src/KraftverkUptime.Api/` | Web-API med Swagger, helse, policies |
| `src/KraftverkUptime.Web/` | Blazor WASM frontend |
| `src/KraftverkUptime.Worker/` | Bakgrunnsprosess for jobber |
| `src/KraftverkUptime.Modules.*/` | Tre tomme modul-prosjekter som fylles i Steg 2 |
| `tests/` | Enhetstester |
| `infra/` | Bicep for Azure |
| `.github/workflows/` | CI og deploy |
| `docker-compose.yml` | Lokal kjøring |
| `azure.yaml` | `azd up`-konfigurasjon |

## Hva du bør gjøre nå

**1. Initialiser git-repo:**
```
cd "C:\Morten\00 Oppetid"
git init
git add .
git commit -m "Steg 1: KraftverkUptime plattformskjelett (.NET 10)"
```

**2. Verifiser at alt bygger og kjører lokalt:**
Følg `docs/verify-step1.md` punkt for punkt. Forventet tid: 15–30 min første gang (hovedsakelig nedlasting av Docker-images og NuGet-pakker).

**3. Når `docker compose up` er grønt og `dotnet test` passerer – du er klar for Steg 2.**

## Hva kommer i Steg 2

Ny chat-sesjon. Jeg trenger da:

- `prompt-2-domene.md` – spesifikasjonen for domenemodulene (finnes ikke i denne sesjonen)
- `drivdal-feb2025-fasit.json` – regresjonsfasit (må hentes fra forrige sesjon eller regenereres)
- Drivdal-rådatafil (avregningsfil) som Python-POC-en brukte

Steg 2 skal fylle ut de tre modul-prosjektene slik at tester mot fasiten passerer:
- Settlement-parser som leser Drivdal-filformatet
- Klassifisering iht. IEEE 762
- KPI-beregning (AF, CF, PlanFulfillment, BidAccuracy)
- XLSX-rapport

## Kjente gjøremål før prod

Ingenting av dette blokkerer Steg 2, men du bør ha det på radaren:

1. Kjør `dotnet ef migrations add Initial` og commit de genererte filene (se `verify-step1.md` steg 3). Uten dette faller oppstart tilbake til `EnsureCreated` med en advarsel.
2. Sett `KEYVAULT_URL` i Azure-miljøet når Entra ID og Key Vault er konfigurert.
3. Bytt `AnonymousUserContextProvider` i Web til en MSAL-basert provider når Entra ID kobles til. Bytt `SystemUserContext` til `EntraIdUserContext` i Api – én linje i `Program.cs`.
4. Skjerp CORS-policy når prod-domene er bestemt.
