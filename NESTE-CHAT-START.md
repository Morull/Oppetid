# Start her i ny chat

## Kontekst
Jeg bygger `KraftverkUptime` – en modulær oppetidsanalyse-app for vannkraft.
Første kunde: Dalane-Kraft. Første anlegg: Drivdal magasinkraftverk (~2,2 MW).
Senere utvidelse til flere verk, SCADA, brukertilgang.

## Status

**Ferdig:**
- Fullstendig prompt-bibliotek i `outputs/`:
  - `prompt-1-plattform.md` – detaljert plattform-spec (.NET 8, Blazor WASM,
    Azure, alle kontrakter og retrofit-sømmer)
  - `prompt-2-domene.md` – hydro-domene-spec (KPI-er, klassifisering,
    settlement-parser)
  - `prompt-oppetidsanalyse-kraftverk.md` – samlet referanse
- Python proof-of-concept kjører på faktiske Drivdal-data:
  - `outputs/drivdal-analyse/` – full Python-implementasjon
  - `outputs/drivdal-feb2025-uptime-report.xlsx` – rapport
  - `outputs/drivdal-feb2025-fasit.json` – regresjons-fasit for .NET

**Fasit (skal matche av .NET-implementasjonen):**
- Total MWh: 703,55 (matcher Summering eksakt)
- Tilstandsfordeling: 370 InService, 163 FO, 67 PO, 51 RS, 21 FD
- AF 62,65 %, CF 47,59 %, PlanFulfillment 93,98 %, BidAccuracy 96,09 %

## Oppgave i denne chatten: Steg 1 – bygg .NET-plattform

Les `outputs/prompt-1-plattform.md` og lever plattform-skjelettet i den
rekkefølgen den spesifiserer:

1. Arkitekturdiagram (Mermaid C4)
2. Solution-skjelett (`dotnet new sln` + alle prosjekter)
3. `KraftverkUptime.Core` – kontrakter og domenetyper
4. `KraftverkUptime.Infrastructure` – EF Core, default-impl av sømmer
5. `KraftverkUptime.Api` – Minimal API med versjonering, policies, helse
6. `KraftverkUptime.Web` – Blazor WASM-shell
7. `KraftverkUptime.Worker` – IJobQueue-host
8. `docker-compose.yml` og `.env.example`
9. Bicep (minimum Azure-infra)
10. GitHub Actions CI/CD
11. README

Når `docker compose up` og `dotnet test` kjører grønt, er Steg 1 ferdig.

## Steg 2 (neste chat etter Steg 1)

Etter plattformen står: les `outputs/prompt-2-domene.md` og implementer
domenemodulene slik at testene matcher `drivdal-feb2025-fasit.json`.

## Viktig

- Følg **leveranseformat**-seksjonen i Prompt 1 strengt: komplette filer,
  full filsti, `docker compose up` skal fungere, ingen placeholders.
- Bekreft de tre oppstarts-spørsmålene i Prompt 1 før du begynner å kode.
- Hvis noe krever designendringer utover prompten, **stopp og spør** –
  ikke gjør stille antagelser.
