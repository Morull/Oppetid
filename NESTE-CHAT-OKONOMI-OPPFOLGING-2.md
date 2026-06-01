# NESTE CHAT — Lukking av SUM-i-kolonner og seksjon-rename

**Dato:** 2026-05-22 (kveld)
**Bygger på:** `NESTE-CHAT-OKONOMI-OPPFOLGING.md` runde 1. To av punktene traff ikke helt — denne instruksen lukker dem.

---

## Punkt 1 — Ekte SUM-i-kolonner på Portefølje-tabellen

### Hva som ble gjort i runde 1

CSS: `tfoot td` fikk `padding: 10px 12px` + `white-space: nowrap`. Det gjør tfoot **like bred som tabellen totalt** (verifisert live: 2609 px), men hver tfoot-celle har fortsatt **feil bredde per kolonne**.

### Hva som fortsatt er feil (live-målt 2026-05-22 kveld)

| Kolonne | Header-celle (x → right) | Tfoot-celle (x → right) | Gap |
|---|---|---|---|
| Anlegg | 280 → 1316 (1036 px) | 280 → 420 (140 px) | 896 px |
| Effekt (MW) | 1316 → 1447 | 420 → 476 | 896 px |
| Produksjon (MWh) | 1608 → 1784 | 552 → 635 («78249,0») | 1056 px |
| Spotoms (NOK) | 1914 → 2070 | 695 → 801 («88 523 600») | 1219 px |
| Reddet vakt (NOK) | 2493 → 2670 | 1102 → 1185 («952 227») | 1391 px |

«88 523 600» (Spotoms-sum) sitter på pixel 695-801, men kolonnen «Spotoms (NOK)» er på pixel 1914-2070. Visuelt fortsatt feil sted.

### Rotårsak

`<td>`-cellene i tfoot arver ikke `width` fra thead/tbody. Bare CSS-padding gjør hver celle litt bredere — den endrer ikke kolonne-tilhørigheten.

### Endring — to fungerende alternativer

**A. Delt `<colgroup>`** (rask CSS-/markup-fiks, lavt risiko):

```razor
<table class="dk-portefolje-table">
  <colgroup>
    <col style="width: 240px" />  @* Anlegg *@
    <col style="width: 110px" />  @* Effekt (MW) *@
    <col style="width: 90px"  />  @* AF *@
    <col style="width: 90px"  />  @* FOR *@
    <col style="width: 140px" />  @* Produksjon (MWh) *@
    @* … én <col> per kolonne … *@
  </colgroup>
  <thead>…</thead>
  <tbody>…</tbody>
  <tfoot>…</tfoot>
</table>
```

`<colgroup>` arves automatisk av alle tre sek­sjonene. Tfoot-cellene legger seg da nøyaktig under sine kolonner.

**B. MudDataGrid `PropertyColumn.Footer`** (mer arbeid, men ryddigere langsiktig):

```razor
<MudDataGrid Items="@_rows">
  <Columns>
    <PropertyColumn Property="@(r => r.Anlegg)" Title="Anlegg"
                    HeaderClass="text-start" CellClass="text-start" FooterClass="text-start">
      <FooterTemplate>SUM</FooterTemplate>
    </PropertyColumn>
    <PropertyColumn Property="@(r => r.SpotomsNok)" Title="Spotoms (NOK)"
                    HeaderClass="text-end" CellClass="text-end" FooterClass="text-end"
                    Format="N0">
      <FooterTemplate>@_sum.SpotomsNok.ToString("N0", _nb)</FooterTemplate>
    </PropertyColumn>
    @* … resten av kolonnene … *@
  </Columns>
</MudDataGrid>
```

Anbefaling: **start med A** (kjapp gevinst, lav risiko). B kan tas senere som del av generell MudDataGrid-konsolidering.

### Akseptkriterier

- [ ] For hver kolonne: `tfoot td`-cellens `x` og `right` matcher tilsvarende `thead th` med < 2 px avvik.
- [ ] SUM-tallene står visuelt på samme vertikale linje som tallene de summerer (sjekkes med 8-kolonne- og 13-kolonne-modus).
- [ ] Tabellbredden endres ikke (ingen horisontal scroll på 1440p+).

---

## Punkt 2 — Seksjon-rename på Rapport-detalj toppkortene

### Hva som ble gjort i runde 1

Code introduserte tre seksjonstitler på toppkortene: **Drift / Marked / Økonomi**, pluss en fjerde seksjon **Megler & avgifter**. Det matcher KPI-katalog-tabellens egne seksjoner lenger ned på siden.

### Hva drifts-leder faktisk ba om

Spec fra `MASTERPLAN-CODE-2026-05-22.md`, formulert av drifts-leder direkte:

> «budleveranse flyttes til **produksjon**. Volum endres til **produksjon**. Marked, **fjernes** og boksene flyttes til **resultat**, samme med megler & avgifter.»

Det gir **tre** seksjoner (ikke fire), med litt andre navn:

| Seksjon | Kort som skal være under |
|---|---|
| **Drift** | Drift-timer · Nedetid · Tilgjengelighet (uendret) |
| **Produksjon** | Produksjon i perioden · Bud-leveranse |
| **Resultat** | Spotomsetning · Ubalansekost · Oppgjør · **KAIA-kostnad** (Megler 1 008,39 + Fast 328,77) |

### Endring

I `ReportDetail.razor` (`HighlightSections` eller tilsvarende):

1. **Rename seksjonstittel:** `Marked` → `Produksjon`. `Økonomi` → `Resultat`.
2. **Fold inn Megler & avgifter:** KAIA-kostnad-kortet legges som siste kort i `Resultat`-seksjonen. «Megler & avgifter» som egen overskrift fjernes.
3. **Behold sortering:** Drift først, Produksjon i midten, Resultat sist.
4. **KPI-katalog-tabellen lenger ned:** *behold* dagens kategorier (Drift / Marked / Økonomi) i den — den følger sannsynligvis et eget kategorisystem fra datamodellen og bør ikke endres her. Hvis ønskelig kan kategorinavn-mapping i `CauseFormatter`/`CategoryFormatter` tas i en egen runde.

### Akseptkriterier

- [ ] Tre seksjonstitler på toppkortene: **Drift**, **Produksjon**, **Resultat**.
- [ ] KAIA-kostnad-kortet ligger under **Resultat**, med sin eksisterende «Megler X + Fast Y»-undertittel som indre dekomponering. Ingen egen «Megler & avgifter»-overskrift.
- [ ] Produksjon i perioden og Bud-leveranse ligger under **Produksjon**.
- [ ] Spotomsetning, Ubalansekost, Oppgjør, KAIA-kostnad ligger under **Resultat**.

---

## Tester

- [ ] Visuell sjekk: SUM-rad på `/portefolje` Sammendrag-fanen — tallene står under sine respektive kolonner i både 8-kolonne- og 13-kolonne-modus.
- [ ] Visuell sjekk: `/reports/{anlegg}/{id}` — tre seksjonstitler på toppen (Drift, Produksjon, Resultat), riktige kort under hver.
- [ ] Regresjons-sjekk: Økonomi-fanen og KPI-katalog-tabellen lenger ned på Rapport-detalj er uendret.

## Estimat

- Punkt 1 alternativ A: 1 time
- Punkt 2: 1-2 timer (CSS-klassifisering + razor-omstrukturering)

Til sammen ~halv arbeidsdag. Kan committes som én patch.
