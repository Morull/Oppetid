# NESTE CHAT — Start/stopp-KPI på Rapport-detalj

**Dato:** 2026-05-22
**Estimat:** 0,5-1 dag
**Bakgrunn:** Drifts-leder ønsker tidlig varsel om driftsendringer som påvirker turbin-/generator-levetid. Hver start/stopp-syklus belaster løpehjul (low-cycle fatigue) — empirisk veletablert i forskningslitteraturen. Se `FAGVURDERING-START-STOPP-VANNVERDI-P50P90.md` for kildeliste.

---

## Mål

Vis en ny KPI-boks under **Drift**-seksjonen på Rapport-detalj (`/reports/{anlegg}/{id}`) som rapporterer antall start/stopp-sykler i rapportperioden, trend mot tilsvarende periode tidligere, og eventuelt slitasje-anslag i NOK.

Ikke en sluttbruker-modell — en monitorerings-KPI som drifts-leder selv vurderer mot OEM-anbefaling.

---

## Datamodell — tre nye felt på `PlantRegistration`

Følg samme mønster som `KaiaAnnualFeeNok` og `NormalAarsproduksjonGwh`:

```csharp
// Antall start/stopp-sykler per år som OEM tillater før akselerert slitasje.
// Settes per anlegg basert på turbinleverandørens dokumentasjon.
public int? StartStoppBudsjettPerAar { get; set; }

// Estimert slitasje-kostnad per syklus. Settes per anlegg basert på
// OEM-vedlikeholdsplan eller egen erfaring. Brukes til å vise NOK-konsekvens.
public double? StartStoppKostnadPerSyklusNok { get; set; }

// Fritekst-felt for begrunnelse/kilde, vises som tooltip i admin.
public string? StartStoppKilde { get; set; }
```

Idempotent skjema-bro i `DatabaseBootstrapper.cs` (samme mønster som `kaia_annual_fee_nok`):

```sql
ALTER TABLE core.plant_registration
ADD COLUMN IF NOT EXISTS start_stopp_budsjett_per_aar integer NULL;
ADD COLUMN IF NOT EXISTS start_stopp_kostnad_per_syklus_nok double precision NULL;
ADD COLUMN IF NOT EXISTS start_stopp_kilde text NULL;
```

EF-mapping i `KraftverkDbContext.cs`. Alle tre **nullbare** — anleggene har ikke disse tallene før drifts-leder fyller inn.

---

## Beregning — definisjon av «syklus»

Bruk eksisterende Elhub-data per time (ikke 15-min, som er for fingranulert for syklus-telling — én startup tar gjerne 10-30 min og kan strekke seg over to 15-min-intervaller).

En **start** = en time hvor `Elhub > tolerance` der forrige time hadde `Elhub ≤ tolerance`.

Standard `tolerance` = 0,5 % av `InstalledCapacityMw` (filtrerer ut støy fra hvilemodus / standby-belastning).

Pseudokode:

```csharp
int CountStarts(IEnumerable<HourlyProduction> hours, double installedMw, double tolerancePct = 0.005)
{
    var threshold = installedMw * tolerancePct;
    int starts = 0;
    bool wasRunning = false;
    foreach (var h in hours.OrderBy(x => x.HourUtc))
    {
        var isRunning = h.MwhElhub > threshold;
        if (isRunning && !wasRunning) starts++;
        wasRunning = isRunning;
    }
    return starts;
}
```

Plasser i `Modules.Reporting/StartStopp/StartStoppCalculator.cs` som ren funksjon, enhetstestbar.

For trend: kjør samme beregning på `PreviousPeriod(from, to, kind)` (samme hjelpefunksjon som Økonomi-fanen bruker).

---

## DTO og endepunkt

Utvid `UptimeReport` eller legg på ny `StartStoppDto`:

```csharp
public sealed record StartStoppDto(
    int AntallStarter,
    int? AntallStarterForrige,
    double? EndringProsent,
    int? BudsjettPerAar,
    int? BudsjettBruktAtd,          // null hvis budsjett ikke satt
    double? BudsjettBruktProsent,   // 0-100
    double? KostnadPerSyklus,
    double? KostnadTotalNok,
    string? Kilde);
```

Returner som del av `UptimeReportDto`. Ingen nytt endepunkt nødvendig — hentes sammen med eksisterende rapport-detalj.

---

## UI — Rapport-detalj, under Drift-seksjonen

I `ReportDetail.razor` (etter at seksjonene Drift / Produksjon / Resultat er på plass per `NESTE-CHAT-OKONOMI-OPPFOLGING-2.md`), legg til en KPI-boks under **Drift**:

```
┌─────────────────────────────────────────┐
│ Start/stopp-sykler                   ℹ  │
│ 12                                      │
│ ▲ +33 % vs forrige måned                │
│ 45 av 80 årsbudsjett (56 %) ▓▓▓▓▓░░░    │
│ ≈ 24 000 NOK slitasje                   │
└─────────────────────────────────────────┘
```

Render-logikk:
- **Hovedtall:** Antall starter i perioden (alltid synlig).
- **Trend-pil:** `EndringProsent` med pil ▲/▼. **Opp = rød** (mer slitasje = dårlig retning).
- **Progressbar med tekst:** Vises kun hvis `BudsjettPerAar` er satt. Beregn `BudsjettBruktAtd` ved å summere starter fra 1. januar til `to`-dato i rapport-perioden (ikke bare perioden). Fargekoding: grønn < 60 %, gul 60-80 %, rød > 80 %.
- **Slitasje-anslag:** Vises kun hvis `KostnadPerSyklus` er satt. Format `≈ XXX NOK slitasje`.
- **Hvis ingen av budsjett/kostnad er satt:** vis en liten lenke «Sett OEM-tall i anleggs-admin» under hovedtallet.

### Info-popup — «Hva er en syklus?»

I kort-headeren (øverst til høyre): en liten «ℹ»-knapp som åpner en `MudPopover` med følgende tekst (helst som en `<KpiInfoPopover>` / `<HjelpeIkon>`-komponent slik at den følger samme mønster som tilsvarende info-popups ellers i appen):

> **Hva er en syklus?**
>
> Én syklus = én komplett sekvens: **oppstart → drift → stopp**. I praksis tilsvarer det «hver gang turbinen starter».
>
> **Det er oppstarts-transienten som skaper slitasjen.** Når turbinen rampes opp fra hvile til nominell effekt, treffer vannet løpehjuls-bladene under raskt endrede strømningsforhold. Det er denne korte fasen som driver «low-cycle fatigue» — sprekkinitiering i bladene. Stoppet bidrar, men i mindre grad.
>
> **Telles ikke:**
> - Korte regulerings-stopp under ca. 30 minutter (tilnærmingen bruker time-oppløsning).
> - Effektregulering uten full stopp (f.eks. ned til 20 % og opp igjen). Det er en *part-load*-belastning, en annen skademekanisme som ikke fanges av denne KPI-en.
>
> **Tallet sporer altså kald-start-sykler spesifikt** — den fysisk mest skadelige hendelsen.
>
> *(Hvis `Kilde` er satt i admin, vises den her som siste linje: «Kilde: {kilde}».)*

Bruk samme info-ikon-mønster (`Icons.Material.Outlined.Info`, `Size.Small`) som de andre KPI-kortene som har fått slike popups. Popoveren skal åpnes ved klikk **og** ved hover, og lukke seg ved klikk utenfor.

---

## Admin-UI

I `PlantAdmin.razor`, rett etter «Normal årsproduksjon (GWh)», legg til tre nye felt:

```razor
<MudNumericField T="int?" Label="Start/stopp-budsjett per år"
                 @bind-Value="_form.StartStoppBudsjettPerAar"
                 HelperText="OEM-anbefaling for antall sykler før akselerert slitasje." />

<MudNumericField T="double?" Label="Slitasje-kostnad per syklus (NOK)"
                 @bind-Value="_form.StartStoppKostnadPerSyklusNok"
                 HelperText="Anslått kostnad per syklus. Hvis ukjent, se tommelfinger-anslag under." />

<MudTextField T="string" Label="Kilde / kommentar"
              @bind-Value="_form.StartStoppKilde"
              HelperText="F.eks. «Voith OEM-manual 2018, kap. 6.3»."
              Lines="2" />
```

Under feltene, vis et lite info-panel:

```
ℹ Tommelfinger-anslag for små Francis-turbiner (1-10 MW) i fravær av OEM-data:
  ca. 400-500 NOK per MW per syklus. For et 5 MW-anlegg gir det ~2000-2500 NOK per syklus.
  Baseres på forsknings-litteratur (Springer 2023, ScienceDirect 2021) — bør erstattes
  med OEM-spesifikke tall så snart de er tilgjengelige.
```

Forklaring av tommelfinger-anslaget i fagvurderingen:

| Anleggsstørrelse | Anslag per syklus | Per MW | Kommentar |
|---|---|---|---|
| 1-2 MW | 500-1 000 NOK | 500 NOK/MW | Mindre masse, enklere lager |
| 3-7 MW | 1 500-3 500 NOK | 400-500 NOK/MW | Hoveddelen av Dalane-porteføljen |
| 8-15 MW | 3 500-7 000 NOK | ca 450 NOK/MW | Større turbiner, ikke nødvendigvis proporsjonalt dyrere |

**Forbehold:** Tallene over er anslag basert på forsknings-litteratur ($200-$250 per syklus i internasjonale studier, omregnet og skalert). De er **ikke** verifisert mot norsk småkraft-OEM-dokumentasjon. Bruk dem som startverdi for å få KPI-en i drift; erstatt med faktiske OEM-tall fra Voith/Rainpower/Andritz/etc. når dere innhenter dem.

---

## Akseptkriterier

- [ ] Tre nye kolonner på `core.plant_registration` etter oppstart (idempotent skjema-bro).
- [ ] EF-mapping i `KraftverkDbContext.cs` for alle tre felt.
- [ ] Admin-UI lar drifts-leder sette og lagre verdiene; tomme felt forblir null.
- [ ] `StartStoppCalculator.CountStarts` er enhetstestet med minst tre scenarioer:
  - Anlegg som starter 5 ganger, stopper 5 ganger → returnerer 5.
  - Sammenhengende drift uten avbrudd → returnerer 1 (eller 0, definer i test).
  - Korte hvilemodus-utslipp under terskel → ignoreres.
- [ ] KPI-boksen rendrer på Rapport-detalj under Drift-seksjonen.
- [ ] Trend-prosent regnes mot tilsvarende periode tidligere via `PreviousPeriod`.
- [ ] Trend-pil er rød ved oppgang (oppoverpil = mer slitasje = dårlig).
- [ ] Progressbar vises kun hvis `BudsjettPerAar` er satt, med fargekoder 60/80 %.
- [ ] Slitasje-anslag vises kun hvis `KostnadPerSyklus` er satt.
- [ ] Hvis verken budsjett eller kostnad er satt: liten lenke til admin synlig.
- [ ] Info-«ℹ»-knapp øverst til høyre i kort-headeren åpner popup med «Hva er en syklus?»-forklaringen.
- [ ] Hvis `Kilde` er satt i admin, vises den som siste linje i popoveren.
- [ ] Tommelfinger-anslag synlig i admin-UI som info-panel under feltene.

---

## Tester

```csharp
[Fact]
public void CountStarts_FiveStartsAndStops_Returns5()
{
    var hours = MakeSequence(0, 1, 1, 0, 1, 1, 0, 1, 1, 0, 1, 0, 1, 0);
    StartStoppCalculator.CountStarts(hours, installedMw: 5).Should().Be(5);
}

[Fact]
public void CountStarts_RunningOnly_Returns1()
{
    var hours = MakeSequence(1, 1, 1, 1, 1);
    StartStoppCalculator.CountStarts(hours, installedMw: 5).Should().Be(1);
}

[Fact]
public void CountStarts_BelowTolerance_Ignored()
{
    var hours = MakeSequence(0, 0.01, 0.01, 0, 1, 1);  // 0.01 MWh under 0.5% av 5 MW = 0.025
    StartStoppCalculator.CountStarts(hours, installedMw: 5).Should().Be(1);
}
```

---

## Implementasjonsrekkefølge

1. **0,1 dag** — `StartStoppCalculator.CountStarts` + tester
2. **0,1 dag** — Datamodell (3 felt + skjema-bro + EF-mapping)
3. **0,1 dag** — Admin-UI med tre felt + info-panel
4. **0,1 dag** — `StartStoppDto` + integrert i `UptimeReportDto`
5. **0,2 dag** — KPI-boks på Rapport-detalj (under Drift, etter at seksjonsstrukturen i `NESTE-CHAT-OKONOMI-OPPFOLGING-2.md` er på plass)
6. **0,1 dag** — Manuell sjekk på Haukland april 2026 + visuell QA

**Sum:** ~0,7 dag rene utviklingstimer, realistisk 1 dag inkludert tester og verifisering.

---

## Avhengighet

Bør committes **etter** `NESTE-CHAT-OKONOMI-OPPFOLGING-2.md` Punkt 2 (Drift / Produksjon / Resultat-seksjoner), siden Start/stopp-boksen skal inn under Drift-seksjonen. Hvis seksjons-renamen ikke er ferdig, plasser boksen midlertidig på toppen og noter at den skal flyttes.

---

## Senere utvidelse (ikke i denne instruksen)

- Vis Start/stopp-trend som 12-mnd-graf på Effektivitet-fanen (drifts-leder kan se utvikling over tid).
- Knytt Start/stopp-tellingen til Vakt-ROI: en start utløst av nedetid-event ≠ planlagt start. Skille mellom de to gir bedre analyse av slitasje fra fleksibel drift vs slitasje fra havarier.
- Auto-varsling når budsjett-prosent passerer 80 % (e-post eller slack).

Disse tas opp som egne instrukser når basis-KPI-en er i drift.
