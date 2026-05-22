# Forbedringsforslag — Info-popup og sortering på tabeller og grafer

**Dato:** 2026-05-22
**Bakgrunn:** Full gjennomgang av alle 10 menysider i kjørende app (`localhost:5180`), inspeksjon av DOM — kolonner, sorterbarhet, tooltips og graf-akser. Skrevet for å gis videre til Claude Code.
**Brukermål (Morten):** Hver tabell og hver graf skal kunne forklares uten forkunnskap. Alle forkortelser skal ha en info-popup. Alle tabellkolonner skal være sorterbare.

---

## Del 1 — De to kravene

1. **Info-popup overalt.** Hver tabell og hver graf skal ha en synlig «ℹ»-affordans som forklarer hva visningen viser og hva forkortelsene betyr. I dag har **ingen** tabell eller graf en slik popup på kort-nivå.
2. **Sortering på alle tabellkolonner.** Hver datakolonne skal være sorterbar. I dag er bare 4 av ~17 tabeller fullt sorterbare. Rene handlingskolonner (ikoner for «Operlog», «Detaljer», rediger/slett) er unntatt.

Begge kravene løses best med **felles, gjenbrukbare komponenter** — ikke side-for-side. Appen har allerede et copy-paste-problem (jf. `NorskTall` i UI-gjennomgangen 20.05); samme forklaringstekst må ikke kopieres inn i 17 filer.

---

## Del 2 — Referansestandard finnes allerede: Portefølje

Portefølje-tabellen er fasiten og bør kopieres som mønster:

- Alle 8 kolonner er sorterbare.
- 7 av 8 kolonneoverskrifter har tooltip (alle unntatt «Anlegg», som ikke trenger forklaring).
- Siden sier eksplisitt i undertittelen: «Hold musepekeren over kolonneoverskriftene for forklaringer.»

Data-import → STATUS-matrisen er også OK: A/T/S/S-kolonnene har tooltips, og det finnes en «Tegnforklaring»-legend.

Resten av appen ligger bak. Målet er å løfte alle sider opp til Portefølje-nivå **og** legge til en kort-popup i tillegg til header-tooltips.

---

## Del 3 — Anbefalt løsning (felles komponenter)

### 3.1 `HjelpeIkon` — info-popup på kort-nivå

Liten «ℹ»-knapp øverst til høyre i hvert kort-hode (tabell og graf). Klikk/hover åpner en `MudPopover` med:

- Én setning om hva visningen viser.
- En kompakt ordliste over forkortelsene som brukes i nettopp denne tabellen/grafen.

```razor
@* Brukes i hvert MudCard-header for tabeller og grafer *@
<MudTooltip Text="@Hjelpetekst" Placement="Placement.Left">
    <MudIconButton Icon="@Icons.Material.Outlined.Info"
                   Size="Size.Small"
                   aria-label="Forklaring" />
</MudTooltip>
```

For lengre forklaringer (flere forkortelser) bruk `MudPopover` i stedet for `MudTooltip`, så teksten kan formateres som en liten liste.

### 3.2 `KolonneHode` — header-celle med tooltip

Pakk hver `MudTableSortLabel` i en `MudTooltip`. Lag én komponent slik at sortering + tooltip alltid følges ad:

```razor
<MudTh>
  <MudTooltip Text="@Forklaring">
    <MudTableSortLabel SortBy="new Func<TRow,object>(x => x.Verdi)">
      @Tittel
    </MudTableSortLabel>
  </MudTooltip>
</MudTh>
```

### 3.3 Én ordliste — felles kilde

Legg alle forklaringer i **én** statisk ordbok (f.eks. `Begreper.cs` med `Dictionary<string,string>`), slik at samme forkortelse alltid får samme tekst. Header-tooltips, kort-popup og eventuelle hover-tekster slår alle opp her. Se Del 5 for ferdig innhold.

### 3.4 Sortering

Bytt hver vanlig `<MudTh>` til en `<MudTh>` med `<MudTableSortLabel>`. For `MudDataGrid`: sett `Sortable="true"` på `Column`. Unntak: rene handlingskolonner.

---

## Del 4 — Full inventar per side

Notasjon: **Sortering** = sorterbare kolonner / totalt. **Header-tooltip** = kolonner med tooltip / totalt. **Info-popup** = kort-popup som forklarer hele visningen.

### Effektivitet — `/effektivitet`

| Visning | Type | Sortering | Header-tooltip | Info-popup | Forkortelser å forklare |
|---|---|---|---|---|---|
| Underytelse — tapstoppliste (episoder) | Tabell, 6 kol | 4/6 — mangler Varighet, Effektområde | 0/6 | Mangler | Δη, kW, kWh, NOK |
| Underytelse per effekt-bånd | Tabell, 6 kol | 0/6 | 0/6 | Mangler | Bånd (kW), Δη |
| Effekt-bins (bredde 200 kW) | Tabell, 3 kol | 0/3 | 0/3 | Mangler | Bin (kW), η |
| Produksjons-intervaller | Tabell, 5 kol | 5/5 ✓ | 0/5 | Mangler | kW, %, m³/s |
| Anleggssammenligning | Tabell (ny seksjon) | Ikke inventert* | — | Mangler | η, kW, m³/kWh |
| η(P)-kurve | Graf | — | — | Mangler | η, P, kW, «Bin-snitt» |

\* Anleggssammenligning lå i lastetilstand under testen (API-ustabilitet fra kodesesjonen). Samme krav gjelder — verifiser når siden er stabil.

### Portefølje — `/portefolje`

| Visning | Type | Sortering | Header-tooltip | Info-popup | Forkortelser |
|---|---|---|---|---|---|
| Anleggsoversikt | Tabell, 8 kol | 8/8 ✓ | 7/8 ✓ | Mangler | MW, AF, FOR, MWh, NOK |

Eneste mangel: kort-popup. Header-tooltips og sortering er allerede på plass — **bruk denne som mal.**

### Nedetid — `/nedetid`

| Visning | Type | Sortering | Header-tooltip | Info-popup | Forkortelser |
|---|---|---|---|---|---|
| Hendelser | Tabell, 9 kol | 6/9 — mangler Årsak (Operlog/Detaljer er handlingskol.) | 1/9 (kun Årsak) | Mangler | t, MWh, NOK |
| Per kategori | Tabell, 4 kol | 0/4 | 0/4 | Mangler | t (timer), NOK |
| Tap per kategori | Graf | — | — | Mangler | Mangler aksetitler |
| Nedetid per uke | Graf | — | — | Mangler | y = Timer |

### Vakt-ROI — `/vakt-roi`

Ingen tabeller eller grafer i standardvisning — kalkulator-side. Har allerede inline «ℹ»-forklaring på vakt-vindu. **OK** — ingen tiltak nødvendig, men hvis resultat-tabell legges til senere gjelder samme krav.

### Capture rate — `/capture-rate`

| Visning | Type | Sortering | Header-tooltip | Info-popup | Forkortelser |
|---|---|---|---|---|---|
| Månedlig oversikt | Tabell, 6 kol | 6/6 ✓ | 0/6 | Mangler | CR, Capture-pris, Baseline, Merverdi |
| Månedlig capture rate | Graf | — | — | Mangler | y = CR |
| Spotpris vs. rå CR | Graf (uten korttittel) | — | — | Mangler | rå CR, NOK/MWh — **mangler også korttittel** |
| Fordeling rå-CR-bin | Graf (uten korttittel) | — | — | Mangler | rå-CR-bin — **mangler også korttittel** |

### Produksjon — `/produksjon`

| Visning | Type | Sortering | Header-tooltip | Info-popup | Forkortelser |
|---|---|---|---|---|---|
| Måneds-trend | Tabell, 7 kol | 7/7 ✓ | 1/7 (kun Timer prod.) | Mangler | Kap.utn., t, Elhub, MWh, Plan-treff, Hydrogrid-merverdi |
| Plan vs. Spotbud vs. Faktisk | Graf | — | — | Mangler | MWh, Elhub, Spotbud |
| Spotpris-utvikling | Graf | — | — | Mangler | NOK/MWh |

### Anlegg — `/plants`

Kort-grid, ingen tabeller/grafer. Kortene viser «Type: Regulated / RunOfRiver» — engelsk sjargong; vurder norsk tekst + kort forklaring (egen, mindre sak).

### Data-import — `/data-import`

| Visning | Type | Sortering | Header-tooltip | Info-popup | Status |
|---|---|---|---|---|---|
| STATUS — dekningsmatrise | Matrise, 11×7 mnd | Ikke relevant (matrise) | Ja på A/T/S/S ✓ | Har Tegnforklaring-legend ✓ | **OK** |
| HISTORIKK — importlogg | Tabell, 8 kol | 0/8 | 0/8 | Mangler | Få forkortelser; trenger sortering |
| MANUELL OPPLASTING | — | — | — | — | Ingen tabeller/grafer |

### Kategorier — `/admin/kategorier`

| Visning | Type | Sortering | Header-tooltip | Info-popup | Forkortelser |
|---|---|---|---|---|---|
| Kategorier | Tabell, 6 kol | 4/6 | 0/6 | Mangler | «#» (antall), «Mapping» |
| Cause-mapping | Tabell, 4 kol | 0/4 | 0/4 | Mangler | «cause-kode» (engelsk) |

### Rapport-detalj — `/reports/{anlegg}/{id}`

| Visning | Type | Sortering | Header-tooltip | Info-popup | Forkortelser |
|---|---|---|---|---|---|
| KPI-katalog (3 deltabeller) | Tabell, 5 kol | 0/5 | 0/5 | Mangler | KPI, «Confidence» (engelsk), Enhet — **KPI-navnene i radene er selve forkortelsene** |
| Klassifiserte timer | Tabell, 8 kol | 0/8 | 1/8 (Driftstilstand) | Mangler | UTC, MWh, Elhub, Spotbud |
| Driftstilstand-fordeling | Graf | — | — | Mangler | Drift/Stille/Tvangs-derating/Nedetid |
| Plan / Spotbud / Faktisk | Graf | — | — | Mangler | Elhub, Spotbud |
| Avvik fra forpliktelse | Graf | — | — | Mangler | «Avvik Plan», «Avvik Spotbud» |

**Spesielt for KPI-katalog:** her er det selve radene (KPI-navnene), ikke kolonneoverskriftene, som er forkortelser. Legg en tooltip på hvert KPI-navn, eller en «ℹ» per rad som slår opp i ordlisten.

---

## Del 5 — Ordliste (felles kilde, klar til bruk)

Legg dette i `Begreper.cs`. Verifiser de markerte (⚠) mot domenekunnskap før publisering.

| Forkortelse | Forklaring |
|---|---|
| η (eta) | Virkningsgrad — andel av tilgjengelig energi som blir til strøm. |
| Δη (delta-eta) | Avvik i virkningsgrad: faktisk η minus forventet η for effekt-bin'en. Negativ = underytelse. |
| P | Effekt (kW). η(P)-kurve = virkningsgrad som funksjon av effekt. |
| kW / kWh | Kilowatt (effekt) / kilowattime (energi). |
| MW / MWh / GWh | Megawatt / megawattime / gigawattime. |
| m³/s | Kubikkmeter vann per sekund (vannføring). |
| m³ per kWh | Spesifikt vannforbruk — vannmengde brukt per produsert kWh. Lavere = bedre. |
| NOK / NOK/MWh / NOK/år | Norske kroner / kroner per MWh / kroner per år. |
| Bin / effekt-bin | Effekt-intervall (her 200 kW bredt) som intervaller grupperes i. |
| AF | Tilgjengelighetsfaktor (Availability Factor) — andel av tiden anlegget var driftsklart. ⚠ verifiser |
| FOR | Tvungen utfallsrate (Forced Outage Rate) — andel av tiden ute pga. uplanlagt feil. ⚠ verifiser |
| CR / Capture rate | Forholdet mellom oppnådd snittpris og referanseprisen (baseline) for perioden. |
| rå CR | Capture rate før filtrering/justering. |
| Baseline | Referansepris perioden måles mot. |
| Merverdi | Kroner tjent ut over baseline. |
| Kap.utn. | Kapasitetsutnyttelse — faktisk produksjon delt på maks mulig i perioden. |
| Plan-treff | Hvor godt faktisk produksjon traff Hydrogrid-planen. |
| Elhub | Norsk datahub for måleverdier; «MwhElhub» = faktisk levert energi. |
| Hydrogrid | Leverandør av produksjonsplan (plan-MWh per time). |
| Spotbud | Budgitt volum mot spotmarkedet. |
| KAIA | Eksport-/kildesystemet importfilene kommer fra. ⚠ verifiser fullt navn |
| SCADA | Driftsovervåkingssystemet (sanntidssignaler fra anlegget). |
| KPI | Nøkkeltall (Key Performance Indicator). |
| UTC | Universaltid — tidsstempler i rapporten er i UTC, ikke norsk tid. |
| t | Timer. |
| Tvangs-derating | Tvungen reduksjon av effekt under maks. |
| A / T / S / S (import-matrise) | Importtyper per måned — har allerede tooltips i appen; gjenbruk de tekstene i ordlisten. |

Engelsk sjargong som bør oversettes til norsk (egne, mindre saker): «Confidence» → «Datakonfidens», «Mapping» → «Kobling», «cause-kode» → «årsakskode», «Regulated/RunOfRiver» → «Regulert/Elvekraft».

---

## Del 6 — Foreslått byggerekkefølge for Claude Code

1. **Ordliste** — opprett `Begreper.cs` med innholdet i Del 5. Én kilde for all forklaringstekst.
2. **`KolonneHode`-komponent** — header-celle som alltid kombinerer `MudTableSortLabel` + `MudTooltip`. Slår opp tekst i ordlisten.
3. **`HjelpeIkon`-komponent** — «ℹ» i kort-hode med `MudPopover`/`MudTooltip` for hele visningen.
4. **Rull ut sortering** — bytt alle vanlige `MudTh` til sorterbare. Mål: hver datakolonne sorterbar (handlingskolonner unntatt). Se Del 4 for hvilke som mangler.
5. **Rull ut header-tooltips** — på alle forkortede kolonner. Portefølje er allerede ferdig.
6. **Rull ut kort-popup** — «ℹ» på hver tabell og hver graf.
7. **Småfiks på grafer** — gi de to navnløse grafene på Capture rate en korttittel; legg aksetitler på «Tap per kategori» (Nedetid).

Punkt 1–3 er fellesarbeidet. Når de er på plass er 4–6 i hovedsak utrulling. Punkt 7 er trivielt og kan tas når som helst.

---

## Merknad om testforhold

Inventaret er hentet mens en kodesesjon pågikk parallelt. Flere sider (Produksjon, Data-import, Rapport-detalj, Effektivitet/Anleggssammenligning) lå tidvis lenge i «Henter data…»-tilstand selv om API-kallene svarte HTTP 200 — sannsynligvis API-restart fra kodesesjonen. Det er ikke logget som feil her, men hvis lange lastetider vedvarer når kodesesjonen er ferdig, bør det undersøkes separat.
