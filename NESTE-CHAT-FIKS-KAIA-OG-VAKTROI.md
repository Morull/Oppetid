# Instruks til Claude Code — fiks KAIA-tilstand + Vakt-ROI-endringer

To uavhengige oppgaver. **Del A må gjøres først** (bygget er i dag ødelagt).
Del B bygger videre på et fungerende bygg.

---

## Før du starter — viktig kontekst

- KAIA-kostnad-funksjonen ble implementert i en tidligere økt, men endte i en
  **halvveis, ucommittet tilstand** i hovedarbeidstreet (`C:\Morten\00 Oppetid`).
- **Ikke kjør `docker compose build` / «Tving full reset» før Del A er ferdig.**
  De kjørende containerne er bygget fra et tidligere komplett øyeblikk; et nytt
  bygg vil feile nå og du mister den kjørende appen.
- Prosjektet bruker **ikke** `dotnet ef migrations`. Skjemaendringer gjøres som
  idempotente `ALTER TABLE ... ADD COLUMN IF NOT EXISTS`-broer i
  `DatabaseBootstrapper.cs` (se `overflow_mode`-mønsteret). Bruk samme mønster.

---

# DEL A — Rydd opp i KAIA-implementasjonen

## A1. Diagnose (verifisert 20.05.2026)

Hele KAIA-arbeidet ligger ucommittet i hovedarbeidstreet. Ingen KAIA-commit
finnes i noen grein. To grunnleggende felt mangler, så koden **kompilerer ikke**:

- `KaiaAnnualFeeNok` refereres av `KraftverkDbContext.cs` og
  `Infrastructure/Reporting/KaiaCostQueryService.cs`, men er **ikke definert** på
  `Infrastructure/Persistence/Entities/PlantRegistration.cs`.
- `MeglerprovisjonNok` refereres av `KaiaCostQueryService.cs` (12 steder) m.fl.,
  men er **ikke definert** på
  `Modules.Settlement/Persistence/SettlementImportRecord.cs`.

(De kompilerte `bin/`/`obj/`-DLL-ene inneholder fortsatt feltene fra et tidligere
vellykket bygg — derfor kjører appen ennå. Kildekoden kan ikke reproduseres.)

Nye, utrackede KAIA-filer som allerede finnes og skal beholdes:
`Api/Endpoints/KaiaCostEndpoints.cs`,
`Infrastructure/Persistence/KaiaMeglerprovisjonBackfillSeeder.cs`,
`Infrastructure/Reporting/KaiaCostQueryService.cs`,
`Modules.Reporting/KaiaCost/` (DTO + interface + `KaiaFeeProration.cs`),
`Web/Services/KaiaCostApi.cs`,
`tests/KraftverkUptime.Infrastructure.Tests/KaiaCost/`.

## A2. Fiks bygget

Les `docs/SPEC-KAIA-KOSTNAD.md` steg 1 og 2, og legg de to manglende feltene
tilbake **nøyaktig slik specen beskriver**:

1. `PlantRegistration.cs` → `public double KaiaAnnualFeeNok { get; set; } = 4000;`
   Bekreft at `KraftverkDbContext.cs` allerede har EF-mappingen (kolonne
   `kaia_annual_fee_nok`) og at skjema-broen finnes i `DatabaseBootstrapper.cs`.
2. `SettlementImportRecord.cs` → `MeglerprovisjonNok` (sjekk specen for
   nøyaktig type — sannsynlig `double?` siden ikke alle imports har feltet).
   Bekreft at `SettlementImport.cs` (EF-entiteten), `DbSettlementImportRecorder.cs`
   og skjema-broen for `meglerprovisjon_nok` er konsistente med dette.

Ikke gjett — hvis noe i specen er uklart for disse to feltene, **stopp og spør Morten**.

## A3. Linjeskift-støy

`git status` viser ~198 endrede filer, men kun ~15 har ekte innholdsendring —
resten er CRLF/LF-støy (`core.autocrlf` er ikke satt). Bekreft selv med:
`git diff HEAD --stat -w --ignore-cr-at-eol`.

Anbefalt håndtering, som **egen, separat commit før KAIA-commiten**:
legg til en `.gitattributes` i repo-roten med `* text=auto eol=lf` (+ `binary`
for png/docx/xlsx/pdf), kjør `git add --renormalize .`, og commit det som
`chore: normaliser linjeskift til LF`. Appen bygges/kjøres i Docker (Linux), så
LF er riktig. Er du usikker på om Morten vil ha repo-vid renormalisering — spør først.

## A4. Bygg, test, verifiser

- `dotnet build` på hele løsningen — skal være grønt.
- Kjør `KaiaFeeProration`-unit-testene — alle skal passere.
- DB-verifisering mot specens fasit ble gjort i forrige økt og stemte (kjent
  avvik: Vikeså + Stølskraft jan–apr mangler i DB, Liavatn −40 NOK på avrunding).
  Du trenger ikke gjenta DB-verifiseringen, men nevn status i commit-meldingen.

## A5. Commit

Commit KAIA-arbeidet som **én ren commit** — stage de KAIA-relaterte filene
eksplisitt etter sti (ikke `git add -A`, for å unngå de 183 støy-filene hvis du
ikke gjorde A3). Forslag til melding:
`feat(kaia): KAIA-kostnad per rapportperiode — meglerprovisjon + pro-rata årsavgift`.

---

# DEL B — Vakt-ROI-endringer

Tre endringer. Avklart med Morten 20.05.2026.

## B1. Override av faktisk varighet på en hendelse

I dag kan drifts-leder kun overstyre overløps-klassifiseringen
(`VaktEventOverrideEntry.Classification` = Auto/HaddeOverlop/IkkeOverlop). Morten
skal i tillegg kunne **korrigere når hendelsen faktisk var over** — for tilfeller
der event-dataene fra SCADA/operlog er feil.

- Utvid `VaktEventOverrideEntry` med et nullbart felt, f.eks.
  `ActualEndOverrideUtc` (`DateTimeOffset?`). Idempotent skjema-bro i
  `DatabaseBootstrapper.cs` for kolonnen.
- Utvid `UpsertVaktOverrideRequest` / `VaktOverrideDto` i `VaktOverrideEndpoints.cs`
  med feltet. Valider i `UpsertAsync`: hvis satt, må `ActualEndOverrideUtc` være
  **etter** `EventStartUtc`. En override-rad kan nå bære klassifisering og/eller
  varighet — begge valgfrie.
- Anvend overstyringen **oppstrøms for `VaktRoiCalculator`**: i koden som setter
  sammen events + overrides (single-plant: `NedetidEndpoints.GetVaktRoiAsync`;
  portefølje: `PortfolioVaktRoiQueryService`), bytt ut `EndUtc` på det aktuelle
  eventet med `ActualEndOverrideUtc` før ROI-beregningen. Da forblir
  `VaktRoiCalculator` en ren funksjon av events — signaturen trenger ikke endres.
  Verifiser at kortere/lengre faktisk varighet flyter riktig gjennom
  outage-intervallene og `EkstraTimerSpart`.

## B2. Splitt «Reddet (NOK)» i overløps-del og ubalanse-del

Dataene finnes allerede: `VaktRoiResultat` har `ReddetProduksjon_NOK` (overløps-/
produksjonsdelen) og `ReddetUbalanse_NOK` (ubalansedelen). Dagens UI viser bare
summen (`ReddetNok`). Gjør de to komponentene synlige:

- **Vakt-ROI portefølje-visning** (per-anlegg-tabellen når intet anlegg er valgt):
  legg til to kolonner — «Overløp (NOK)» og «Ubalanse (NOK)» — ved siden av
  «Reddet (NOK)». Utvid `PortfolioVaktRoiPlantSummary` (og evt.
  `PortfolioVaktRoiTopEvent`) med de to komponentene, og summer dem i
  `PortfolioVaktRoiQueryService`.
- **Alle hendelser i Vakt-ROI** (single-plant-tabellen «Hendelser med ROI-
  vurdering»): vis overløps-delen og ubalanse-delen per hendelse — som kolonner
  og/eller i popup-en fra B3. `VaktRoiResultat` har allerede feltene, så her
  trengs bare UI.

Behold «Reddet (NOK)» som totalsum i tillegg.

## B3. Per-hendelse popup i «Hendelser med ROI-vurdering»

Erstatt dagens inline Override-nedtrekksliste i hendelses-tabellen
(`VaktRoi.razor`, kolonnen rundt linje 535) med en **«Detaljer»-knapp** per rad
som åpner en `MudDialog`. Bruk `Components/AnnotationDialog.razor` som mønster.

Popup-en skal samle alt for én hendelse:

- **Hendelsesinfo:** start, slutt, faktisk varighet, kategori, årsak (cause-kode
  via `CauseFormatter`), vakt-vindu ja/nei, reddbar ja/nei.
- **Økonomi-breakdown:** tap (MWh + NOK fra `DowntimeEvent`), ekstra timer spart,
  overløp-timer i counterfactual, reddet produksjon (NOK), reddet ubalanse (NOK),
  reddet totalt (NOK), og forklaringsteksten (`VaktRoiResultat.Forklaring`).
- **Overstyringer (skrivbart):**
  - Overløps-klassifisering: Auto / Hadde overløp / Ikke overløp (dagens valg).
  - **Faktisk slutt / varighet** (nytt, fra B1) — la brukeren skrive inn hvor
    lenge hendelsen faktisk varte; lagre som `ActualEndOverrideUtc`. Vis avledet
    varighet som lesbar verdi.
  - Kommentar (fritekst).
  - Lagre-knapp → `PUT .../vakt-overrides`, deretter reload av Vakt-ROI-dataene.

## B4. Bygg, test, commit

- `dotnet build` grønt; eksisterende `VaktRoiCalculatorTests` skal fortsatt passere.
- Legg gjerne til en test som bekrefter at `ActualEndOverrideUtc` endrer
  `EkstraTimerSpart` som forventet.
- Commit Del B separat fra Del A, f.eks.
  `feat(vakt-roi): per-hendelse popup + varighet-override + reddet-splitt`.

---

## Akseptkriterier

- [ ] `dotnet build` på hele løsningen er grønt.
- [ ] `KaiaFeeProration`- og `VaktRoiCalculator`-testene passerer.
- [ ] KAIA-arbeidet er committet; ingen utrackede KAIA-filer igjen.
- [ ] Linjeskift-støyen er håndtert (egen commit) eller bevisst utelatt.
- [ ] Vakt-ROI: popup åpnes per hendelse med full økonomi-breakdown.
- [ ] Popup kan endre faktisk varighet, og ROI oppdateres deretter.
- [ ] Overløps-del og ubalanse-del vises både i portefølje-visningen og på
      hver hendelse.
- [ ] Containerne kan bygges på nytt uten feil.

## Sidenotis (ikke kritisk)

Repoet har fire foreldede `claude/...`-worktrees merket `prunable`
(`git worktree list`). De kan ryddes med `git worktree prune` når du er ferdig —
men sjekk med Morten først at ingen av dem har ucommittet arbeid.
