# Testrapport — Oppetid (KraftverkUptime)

**Dato:** 2026-05-21
**Testet av:** Claude (Cowork), via Chrome mot kjørende app på `localhost:5180`
**Metode:** Smoke-test av alle menysider + UI-gjennomgang mot `ANBEFALINGER-UI-GJENNOMGANG.md` (20.05) + inspeksjon av import-modulen. Funn er hentet fra DOM, konsoll-logg og nettverkskall — ikke kun skjermbilder.
**Merknad:** Testet samtidig som en aktiv kodesesjon i Claude Code. Funn #2 ligger sannsynligvis på siden som ble kodet i øyeblikket og kan være work-in-progress.

---

## Sammendrag

| # | Funn | Alvorlighet | Side |
|---|------|-------------|------|
| 1 | Rapport-detalj krasjer på Kaia-kostnad (`nb-NO` kultur i invariant-modus) | **Høy** | Rapport-detalj |
| 2 | Effektivitet-siden krasjer i ApexCharts (`NullReferenceException`) | **Høy** | Effektivitet |
| 3 | Inkonsistent tallformatering mellom sider (komma vs. mellomrom) | Middels | Flere |
| 4 | Synlig fokus-ramme rundt sidetittelen etter navigasjon | Lav | Alle |

Smoke-test: **8 av 10** menysider laster feilfritt. 2 sider (Rapport-detalj, Effektivitet) utløser Blazors globale feilbanner. Alle API-kall svarte 200 — ingen 404/500 i nettverkslaget.

Tre punkter fra UI-gjennomgangen 20.05 er **utbedret** siden den ble skrevet (se eget avsnitt).

---

## Funn #1 — Rapport-detalj krasjer på Kaia-kostnad

**Alvorlighet:** Høy · **Side:** `/reports/{anlegg}/{id}` (og `/reports`, som redirecter hit)

Reproduserbart ved hver lasting. Konsollen kaster:

```
System.Globalization.CultureNotFoundException:
  Argument_CultureNotSupportedInInvariantMode — Argument_CultureInvalidIdentifier, nb-NO
   at System.Globalization.CultureInfo..ctor(String)
   at KraftverkUptime.Web.Pages.ReportDetail.FormatNok(Nullable`1 v)
   at KraftverkUptime.Web.Pages.ReportDetail.FormatKaiaTotal(KaiaCostDto k)
```

**Årsak:** Blazor WASM-appen kjører med `InvariantGlobalization` aktivert (ingen ICU-data lastes — bekreftet i nettverkslaget). `ReportDetail.FormatNok` kaller `new CultureInfo("nb-NO")`, som er ulovlig i invariant-modus og kaster.

**Effekt:** Kaia-kostnadskomponenten rendres ikke, og Blazors globale feilbanner («En uventet feil har oppstått / Last inn på nytt») vises nederst på alle rapporter. Resten av rapporten rendres.

**Forslag til fiks (én av):**
- Fjern `new CultureInfo("nb-NO")` i `FormatNok` og bruk en eksplisitt `NumberFormatInfo` (mellomrom som tusenskille, komma som desimaltegn) bygd manuelt — uavhengig av kultur. Dette er den samme tilnærmingen de andre sidene allerede bruker (se funn #3).
- Eventuelt: skru av `InvariantGlobalization` i `KraftverkUptime.Web.csproj` og inkludér ICU. Dyrere (større nedlasting), men løser rotårsaken globalt.

Henger sammen med UI-gjennomgangens punkt 2.9 («NorskTall er kopilimt inn i minst fire filer»).

---

## Funn #2 — Effektivitet-siden krasjer i ApexCharts

**Alvorlighet:** Høy · **Side:** `/effektivitet`

Konsollen kaster ved init og ved opprydding når man forlater siden:

```
System.NullReferenceException: Arg_NullReferenceException
   at ApexCharts.ApexPointSeries`1[[KraftverkUptime.Web.Services.EffektivitetBin]].OnInitialized()
   at ApexCharts.ApexPointSeries`1[[KraftverkUptime.Web.Services.EffektivitetBin]].Dispose()
```

**Effekt:** Feilbanneret vises. Siden rendrer KPI-kort og η(P)-kurve, men en `ApexPointSeries<EffektivitetBin>`-komponent feiler under initialisering.

**Sannsynlig årsak:** Et `<ApexPointSeries>`-element bindes til en data-/`Items`-kilde som er `null` på tidspunktet `OnInitialized` kjører — typisk at serien rendres før dataene er hentet. Verifiser at serie-komponenten ligger bak en `@if (data is not null)`-sjekk, eller initialisér samlingen til en tom liste.

**Merknad:** Dette er etter alt å dømme siden som ble kodet under testen (effektivitet 15-min). Verifiser om dette allerede er fanget i pågående arbeid før det logges som ny feil.

> **⚠️ Oppdatering 2026-05-21 11:52 — fortsatt åpen.** Re-testet live etter at appen ble stoppet og startet på nytt: krasjet og feilbanneret er **fremdeles til stede**. Hvis dette ble antatt fikset: bekreft at endringen faktisk ligger i kildekoden, og bygg på nytt med `docker compose build` (ikke bare `up`). Egen detaljert plan i `FORBEDRINGSFORSLAG-EFFEKTIVITET.md`.

---

## Funn #3 — Inkonsistent tallformatering mellom sider

**Alvorlighet:** Middels · **Side:** flere

Samme talltype formateres ulikt avhengig av side:

| Side | Eksempel | Stil |
|------|----------|------|
| Rapport-detalj (KPI-kort) | `2,266,525 NOK` · `98.1 %` | Komma som tusenskille, punktum som desimal (invariant/US) |
| Portefølje / Nedetid / Produksjon | `20 227 643 NOK` · `715 980` | Mellomrom som tusenskille (norsk) |
| Capture rate | `1,06` · `0,99` | Komma som desimal (norsk) |

Konkret: Hauklands spotomsetning for april vises som `2,266,525` på rapport-detalj, men `2 266 525` på Portefølje — samme tall, to ulike formater.

**Årsak:** Samme rot som funn #1 og UI-punkt 2.9 — `NorskTall`-formateringen er kopiert inn i flere filer i ulike varianter. Bør samles i én delt hjelpeklasse som bygger en eksplisitt `NumberFormatInfo` (mellomrom + komma), kultur-uavhengig.

---

## Funn #4 — Synlig fokus-ramme rundt sidetittelen

**Alvorlighet:** Lav (kosmetisk) · **Side:** alle

Sidetittelen (`<h1 class="dk-page-title">`) får programmatisk fokus ved hver ruteendring — bra for skjermlesere — men viser nettleserens standard fokus-ramme (`outline: 1.5px auto`). Det tegner en rektangulær ramme rundt hele sidehodet til brukeren klikker noe. Synlig i både mørk og lys modus.

**Forslag:** Bruk `:focus-visible` i stedet for `:focus` på `.dk-page-title`, eller sett `outline: none` for programmatisk fokus. Behold selve fokuseringen — det er bare den visuelle rammen som bør dempes.

---

## UI-gjennomgang — status mot anbefalingene fra 20.05

**Utbedret siden gjennomgangen ble skrevet:**

- **Punkt 2.8** — Effektivitet ligger nå i venstremenyen (var «foreldreløs»).
- **Punkt 2.9** — `.dk-subbar` ligger nå på `top: 64px` (var 56px). Periodevelgeren er visuelt riktig justert mot toppbaren.
- **Del 1** — Portefølje-tabellen er kuttet til 7 synlige kolonner med en «Vis alle kolonner»-bryter, i stedet for 13 kolonner med vannrett scrolling.

**Fortsatt til stede:**

- **Punkt 2.10** — Den globale periodevelgeren henger fortsatt. Bekreftet oppførsel: etter et besøk på en rapport står velgeren i «Egendefinert» med ←/→-pilene grået ut. Modusen «smitter» til andre sider — testet konkret: satte Nedetid til «Måned» (pilene ble aktive), besøkte en rapport, gikk tilbake til Nedetid → tilbake i «Egendefinert» med døde piler. Bruker må manuelt klikke «Måned» for å få stegningen tilbake.

Øvrige punkter i gjennomgangen (KPI-kort-konsolidering, fargetokens, felles `PageHeader`, typografiskala) er strukturelle og ble ikke verifisert linje-for-linje i denne testen.

---

## Smoke-test — alle menysider

| Side | URL | Resultat |
|------|-----|----------|
| Rapporter | `/reports` | Redirecter til rapport-detalj → **funn #1** |
| Portefølje | `/portefolje` | OK |
| Nedetid | `/nedetid` | OK |
| Vakt-ROI | `/vakt-roi` | OK (redirecter til `/vakt-roi/haukland`) |
| Capture rate | `/capture-rate` | OK |
| Produksjon | `/produksjon` | OK |
| Effektivitet | `/effektivitet` | **Funn #2** |
| Anlegg | `/plants` | OK |
| Data-import | `/data-import` | OK |
| Kategorier | `/admin/kategorier` | OK |

`/reports` redirecter rett til siste rapport-detalj — liste-visningen vises i praksis aldri (jf. UI-punkt 2.10.1).

---

## Import / CSV

Import-modulen ble inspisert, men ingen ny fil ble lastet opp — for ikke å skrive testdata inn i databasen mens kodesesjonen pågår.

- **Status-fanen:** dekningsmatrise (anlegg × måned) rendrer riktig. 148 komplette av 208 forventede, 24 delvise, 36 forfalte.
- **Manuell opplasting:** alle 5 opplastingsseksjoner rendrer (enkelt-anlegg, multi-anlegg, operlog, SCADA-trender, per-anlegg). Ingen konsollfeil.
- **Historikk:** importloggen viser mange vellykkede importer med 100 % dekning. Auto-import / hot-folder er aktiv (50 importer mottatt siste 24 t).
- **Observasjon:** én fil ligger i karantene — `dataeksport_20260521101052.xlsx`, «Feil: The operation was canceled.». Dette skyldes mest sannsynlig at API-et ble restartet av kodesesjonen midt i en import. Verifiser, men trolig forbigående.

Konklusjon: import-pipelinen fungerer ende-til-ende. En live opplastingstest kan kjøres på forespørsel.

---

## Tekniske merknader

- Alle API-kall mot `localhost:5080` svarte HTTP 200. Ingen 404/500.
- Lys modus ble testet — tekst er lesbar, ingen usynlig tekst observert på de sidene som ble sjekket.
- Konsoll-støy av typen «A listener indicated an asynchronous response…» kommer fra en Chrome-utvidelse, ikke fra appen, og er holdt utenfor funnene.
