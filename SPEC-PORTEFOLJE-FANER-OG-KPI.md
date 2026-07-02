# Spec: Portefølje-faner og KPI-matrise

**Status:** Klar til implementasjon  
**Dato:** 2026-06-12  
**Prioritet:** UI-forbedring — ingen API-endringer nødvendig for tab-flytt

---

## 1. Endringer i navigasjon

### 1a. Flytt Månedsrapporter fra "Data & Admin" til Portefølje

**NavMenu.razor:**  
- Fjern `<MudNavLink Href="reports" ...>Månedsrapporter</MudNavLink>` fra "Data & Admin"-gruppen  
- Behold `/reports`-ruten uendret (bokmerker og direkte-URL-er skal fortsatt virke)

### 1b. Legg til "Månedsrapport"-fane på Portefølje

**Portefolje.razor — MudTabs-blokken:**  

Nåværende tilstand: 2 faner (Sammendrag = indeks 0, Økonomi = indeks 1)  

Ny tilstand: 3 faner:
```
[Sammendrag]  [Økonomi]  [Månedsrapport]
    0              1            2
```

Fane-index-referansene i `@code`-blokken må oppdateres:
- `OnTabChangedAsync`: legg til `if (index == 2 && ...)` for lazy-load av Månedsrapport
- `LoadAsync()`: `if (_activeTab != 0) return;` er allerede korrekt (kun Sammendrag-fanen henter data her)

**Innholdet i Månedsrapport-fanen:**  
Gjenbruk eksisterende `<ReportPageSelector />` + `<MudTable>`-logikk fra `Reports.razor`, men uten redirect-logikken (brukeren er allerede på Portefølje-siden og forventer ikke å bli navigert bort).

Enkleste implementasjon: lag en ny `MånedsrapportFane.razor`-komponent som er en forenklet versjon av Reports.razor:
- Anleggs-velger (AnleggVelger-komponent, lik Økonomi-fanen)
- Liste over importerte avregninger for valgt anlegg + periode
- Klikk på rad → naviger til `/reports/{PlantId}/{IdempotencyKey}` (eksisterende ReportDetail)
- Ingen auto-redirect (vis listen direkte)

```razor
@* MånedsrapportFane.razor — ny komponent *@
<AnleggVelger Plants="@Filter.Plants" SelectedPlantsChanged="OnPlantChanged" />
@if (_items is null)
{
    <MudProgressCircular Indeterminate="true" />
}
else if (_items.Count == 0)
{
    <MudAlert Severity="Severity.Info">Ingen importerte rapporter for valgt anlegg og periode.</MudAlert>
}
else
{
    <MudTable Items="_items" Hover="true" OnRowClick="OnRowClick" ...>
        @* Samme kolonner som Reports.razor: Anlegg, Periode, Timer, Avvik, Importert, Status *@
    </MudTable>
}
```

---

## 2. KPI-matrise — hva skal vises hvor

### Brukers krav (fra Oppetid.txt)

| KPI | Månedsrapport | Portefølje | Økonomi |
|-----|:---:|:---:|:---:|
| CR (Capture Rate) | – | X | X |
| Timing verdi (Merverdi) | – | X | X |
| Snitt virkningsgrad | – | – | – |
| Spot bud treff | X | – | – |
| % treff Toppris perioder | – | – | – |
| Ubalanse kost | X | – | X |
| Produksjonstimer | – | – | – |
| Vakt – Reddet brutto | – | X | X |
| Vakt reddbare hendelser | – | X | – |
| Start stopp sykluser | – | – | – |

### Nåværende tilstand (hva som allerede er der)

**Portefølje/Sammendrag:**  
Allerede KPI-kort: Produksjon (MWh), Spotomsetning, Nedetidstap, Reddet av vakt  
Allerede i tabell ("Vis alle kolonner"): CR, Merverdi, Nedetid, Tap, Reddet vakt, Reddbare  
→ CR og Timing verdi er teknisk sett der, bare ikke som KPI-kort øverst

**Økonomi:**  
Layout (fra OkonomiFane.razor):  
- Inntekter: Spotomsetning, Capture rate, Merverdi vs spot  
- Kostnader: Ubalansekost ✓, KAIA-kostnad, Vakt-kost  
- Resultat: Oppgjør, Nedetidstap, Reddet av vakt ✓  
→ CR, Timing verdi (Merverdi), Ubalansekost og Vakt Reddet brutto er ALLEREDE der

**Konklusjon:** Økonomi-fanen oppfyller allerede brukers matrise for den kolonnen.  
Portefølje/Sammendrag har CR og Merverdi i tabellen, men ikke som KPI-kort.

### Hva som faktisk mangler

| KPI | Hva som mangler | Prioritet |
|-----|----------------|-----------|
| CR i Portefølje-KPI-kort | Løft CR fra "vis alle kolonner"-tabellen til KPI-strip | Lav – tabellen dekker behovet |
| Spot bud treff i Månedsrapport | Nytt felt som viser bud-treff fra settlement-data | Medium – krever backend |
| Ubalanse kost i Månedsrapport | Finnes i settlement → vis fra `PerPlantEconomyDto.UbalansekostNok` | Lav – allerede i ReportDetail |

---

## 3. Mine egne anbefalinger (utover brukers matrise)

Basert på hva som gir verdi for en driftsleder vs forvalter:

| KPI | Forslag | Begrunnelse |
|-----|---------|-------------|
| **% treff Toppris perioder** | Økonomi + Månedsrapport | Direkte linket til Capture Rate — naturlig å se sammen |
| **Snitt virkningsgrad** | Månedsrapport | Operasjonelt detaljnivå, ikke relevant i portefølje-oversikt |
| **Produksjonstimer** | Sammendrag (allerede MWh — legg til timertall som sub-text) | Trivielt å vise, gir kontekst |
| **Start/stopp sykluser** | Månedsrapport | Maskinslitasje-indikator — hører hjemme i månedlig driftsgjennomgang |
| **Vakt reddbare hendelser** | Månedsrapport (i tillegg til Sammendrag) | Nyttig for månedlig oppfølging |

**Oppdatert anbefalt matrise:**

| KPI | Månedsrapport | Portefølje | Økonomi |
|-----|:---:|:---:|:---:|
| CR | – | X (tabell) | X (kort) |
| Timing verdi / Merverdi | – | X (tabell) | X (kort) |
| **Snitt virkningsgrad** | **X** | – | – |
| Spot bud treff | X | – | – |
| **% treff Toppris perioder** | **X** | – | **X** |
| Ubalanse kost | X | – | X (kort) |
| **Produksjonstimer** | – | **X (sub-text)** | – |
| Vakt – Reddet brutto | – | X (kort) | X (kort) |
| Vakt reddbare hendelser | **X** | X (tabell) | – |
| **Start/stopp sykluser** | **X** | – | – |

*Fet = mitt tillegg. X (tabell) = allerede der, ikke i eget KPI-kort.*

---

## 4. Konkret implementasjonsplan (rekkefølge)

### Steg 1: Ny fane-struktur (UI, ingen backend) — 1-2 timer
1. Lag `MånedsrapportFane.razor` (enkel komponent basert på Reports.razor)
2. Legg til fanen i `Portefolje.razor` som tab-indeks 2
3. Fjern nav-link fra "Data & Admin" i `NavMenu.razor`
4. Oppdater tab-index-logikk i `Portefolje.razor @code`

### Steg 2: Spot bud treff i Månedsrapport — 1-2 timer backend
Bud-treff = andel timer der faktisk produksjon ≥ produksjonsplan × terskel.  
Sjekk om `PerPlantEconomyDto` eller `ReportDetail`-API-et allerede returnerer dette.  
Hvis ikke: nytt felt i `ClassifiedHourlyReport` og `EconomyReportDto`.

### Steg 3 (valgfritt): Løft CR og Timing verdi til KPI-kort i Sammendrag — 30 min
Legg til to KPI-kort under eksisterende fire i Portefolje.razor:
```razor
<MudItem xs="12" sm="6" md="3">
    <KpiCard Label="Capture Rate (snitt)"
             Value="@FormatCr(CrVektetSnitt())"
             SubText="Vektet etter spotomsetning"
             Accent="KpiAccent.Neutral" />
</MudItem>
<MudItem xs="12" sm="6" md="3">
    <KpiCard Label="Timing-verdi (Merverdi)"
             Value="@FormatNokSigned(_rows?.Sum(r => r.MerverdiNok) ?? 0)"
             Accent="@MerverdiAccent(MerverdiSum())" />
</MudItem>
```

---

## 5. Filer som endres

| Fil | Endring |
|-----|---------|
| `NavMenu.razor` | Fjern Månedsrapporter fra Data & Admin |
| `Portefolje.razor` | Legg til tab 2, juster tab-index-logikk |
| `MånedsrapportFane.razor` (ny) | Komponent for ny fane |
| `ReportDetail.razor` | Ingen endringer — eksisterende side beholdes som drill-down |

---

## 6. Ikke i scope for denne specen

- Backend-endringer for nye KPI-er (Spot bud treff, virkningsgrad, toppris-treff) — egne specs
- Endringer i `/reports`-ruten (beholdes for bokmerker og direkte-tilgang)
- Endringer i ReportDetail (per-anlegg månedlig drilldown er allerede komplett)
