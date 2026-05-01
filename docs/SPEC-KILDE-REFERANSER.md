# Spec: Korriger kilde-referanser (KAIA + KraftScada)

**Dato:** 2026-04-29
**Estimat:** 1-2 t (mest søk-og-erstatt + dokumentasjons-rydding)
**Bakgrunn:** Nåværende kodebase og dokumentasjon refererer feilaktig til "eSett-portalen", "Statnett/Elhub-eksport", og "NVE-rapportering" som om disse er kildene Dalane Kraft eksporterer fra. **Faktiske kilder:**

| Datatype | Faktisk kilde | Tidligere referert som |
|---|---|---|
| Settlement Excel (ubalanse, RK, oppgjør, spotpris) | **KAIA** | "eSett-portalen" / "NVE/Statnett-eksport" |
| SCADA master-CSV (effekt, vannføring, magasin, virkningsgrad) | **KraftScada** | "Drivdal SCADA-eksport" (anlegg-spesifikt) |
| Operlog-CSV (alarm-events) | **KraftScada** | "operlog-export" (anlegg-spesifikt) |

KAIA er kraftbransjens portal for oppgjørs-data — den AGGREGERER fra eSett/Elhub/Nord Pool, men er det Dalane Kraft som bruker som ETT integrasjonspunkt. KraftScada er SCADA-leverandøren.

## Hva som SKAL endres

**Beskrivelser av hvor data kommer fra** i dokumentasjon og kommentarer.

## Hva som IKKE skal endres

**Kolonnenavn i Excel-data** og tilsvarende C#-property-navn:
- `MwhElhub`, `MwhESett`, `SpotbudMwh`, `RkPrisNokMwh`, `ESettVolumgebyrNok` osv.
- Disse matcher det KAIA selv kaller dataene (siden KAIA aggregerer fra eSett/Elhub) og kan ikke endres uten å bryte parser-en.

**Kolonner i Excel-fila**: `MWh-Elhub`, `MWh-eSett`, `eSett volumgebyr` osv. — disse er KAIA sine valgte navn.

## Endringer per fil

### Dokumentasjons-filer (kommentarer i forklaring)

**Fil:** `prompt-2-domene.md`
- Linje 12: "Norsk kraftmarkedstrukturen: Elhub, eSett, Nord Pool, regulerkraft, ubalanse­oppgjør, NVE-rapportering."
  → "Norsk kraftmarkedstrukturen: KAIA-portalen (oppgjørsdata), KraftScada (drifts-data), Nord Pool, regulerkraft, ubalanse­oppgjør."
- Linje 56: "Settlement (Elhub/eSett-eksport via portal)" → "Settlement (KAIA-eksport)"

**Fil:** `prompt-oppetidsanalyse-kraftverk.md`
- Linje 62: "RegulatoryReportModule (NVE/Statnett-rapportering)" → "RegulatoryReportModule (KAIA-rapportering)"
- Linje 136: "Faktisk datakilde (oppgjørsdata fra Nord Pool / Elhub / eSett)" → "Faktisk datakilde (oppgjørsdata fra KAIA-portalen)"
- Linje 402: "fremfor direkte Elhub/eSett API-er" → "fremfor direkte KAIA API-er (når disse kommer)"
- Linje 685: "eksponere ... til Statnett, leverandører" → "eksponere ... til KAIA, leverandører"

**Fil:** `LEVERANSE-STEG2.md`
- Linje 81: "klar for Nivå 1 (hydrologi) når NVE Sildre-integrasjon" → "klar for Nivå 1 (hydrologi) når Sildre-integrasjon" (NVE-Sildre er fortsatt korrekt for hydrologi-data, så bare vurder om det skal beholdes)

**Fil:** `drivdal-analyse/README.md`
- Linje 4: "Elhub/eSett-eksport fra portal" → "KAIA-eksport"

**Fil:** `OVERLEVERING-2026-04-26.md`
- Linje 147: "Nettsidefeil (Statnett)" → "Nettsidefeil" (fjern Statnett-spesifikk referanse, eller behold som teknisk korrekt)

### Kode-kommentarer (XML-doc + line-kommentarer)

Søk gjennom hele `src/`-treet etter strings som beskriver datakilde:

```powershell
# Eksempel-søk
Get-ChildItem -Path src -Recurse -Filter *.cs | Select-String "eSett-portal|Elhub-eksport|Statnett-eksport|NVE-rapport"
```

Erstatt slik (sjekk hver match individuelt — noen vil være kolonnenavn som IKKE skal endres):

| Før (kommentar) | Etter |
|---|---|
| "eSett-portalen" | "KAIA-portalen" |
| "eksport fra Elhub" | "eksport fra KAIA" |
| "Statnett-data" | "KAIA-data" (når kontekst er settlement) |
| "Drivdal SCADA-system" | "KraftScada" (når generelt) |
| "operlog fra SCADA" | "operlog fra KraftScada" |

### Specifikke filer å sjekke

**Settlement-side:**
- `src/KraftverkUptime.Modules.Settlement/Parsing/ExcelSettlementParser.cs` — XML-doc om "format fra eSett-portalen" → "fra KAIA-portalen"
- `src/KraftverkUptime.Modules.Settlement/Dtos/SettlementHourlyRow.cs` — kommentarer om datakilde
- `src/KraftverkUptime.Api/Endpoints/SettlementsEndpoints.cs` — XML-doc

**SCADA-side:**
- `src/KraftverkUptime.Modules.Scada/Import/ScadaMasterCsvParser.cs` — XML-doc om "Drivdal-format" → "KraftScada master-format"
- `src/KraftverkUptime.Modules.Scada/Import/OperlogCsvParser.cs` — XML-doc om "Drivdal-format operlog-CSV" → "KraftScada operlog-CSV"
- `src/KraftverkUptime.Modules.Scada/Import/IScadaImportService.cs` — kommentarer

**Domene:**
- `src/KraftverkUptime.Core/Domain/SignalMap.cs` — XML-doc om "SCADA-tags" beholdes generelt (ikke leverandør-spesifikk)

### UI-tekster

Søk i Razor-sider etter brukervendte strings:

```powershell
Get-ChildItem -Path src/KraftverkUptime.Web -Recurse -Filter *.razor | Select-String "eSett|Elhub|Statnett|NVE"
```

Eksempler:
- `Pages/Upload.razor` — beskrivelses-tekst over upload-skjema
- `Pages/Plants.razor` — kort-beskrivelser
- Tooltip-tekster

## Implementasjons-strategi

1. **Søk først, ikke endre blindt.** Mange "Elhub" og "eSett" er kolonnenavn som IKKE skal endres.
2. **Skill mellom DATAKILDE-beskrivelse og DATAFELT-navn:**
   - "Data hentes fra eSett-portalen" → endres til KAIA
   - `MwhElhub`-property → IKKE endres
   - "MWh-Elhub" som tekst i UI ved kolonne-overskrift → IKKE endres (det er hva fila heter)
3. **Kommentarer kan være verbose.** Eksempel:
   ```csharp
   /// <summary>
   /// Parses settlement Excel-eksport fra KAIA-portalen (Dalane Kraft sin
   /// oppgjørsleverandør). KAIA aggregerer underliggende data fra Elhub
   /// (målt produksjon), eSett (ubalanse-oppgjør), og Nord Pool (spotpris).
   /// Kolonnenavn i Excel beholder eSett/Elhub-terminologi siden det er
   /// KAIA sin valgte navngiving.
   /// </summary>
   ```

## Tester

Ingen funksjonelle endringer — så ingen nye tester. Eksisterende tester skal fortsatt passere uendret.

## Verifisering

Etter endringer:

```powershell
dotnet build KraftverkUptime.sln
dotnet test
```

Forventet: alt grønt, ingen tester brutt.

Søk for å bekrefte at intet sentralt sted refererer til "eSett-portalen" som datakilde:

```powershell
Get-ChildItem -Path src,docs -Recurse -Include *.cs,*.md,*.razor | Select-String "eSett-portalen|Elhub-eksport|NVE-rapport|Statnett-eksport"
```

Forventet: 0 treff i `src/`. I `docs/` kan det fortsatt finnes historiske referanser i `prompt-*.md` som kan beholdes som kontekst.

## Out-of-scope

- Endre `eSett-portal-v1` schema-streng — beholdes for bakoverkompatibilitet
- Endre kolonnenavn i Excel-export
- Endre eksterne API-spec-dokumenter (de er låst mot KAIA sin navngiving)
- Skill mellom "KAIA Web" (manuell eksport) og "KAIA API" (automatisk integrasjon kommer i v2)

## Begrunnelse

Drifts-leder og fremtidige operatører trenger å se i dokumentasjonen hvilken portal de skal logge inn på når de vil hente nye eksporter. "eSett-portalen" er teknisk korrekt for komponentene under, men misvisende fordi Dalane Kraft ikke logger inn der — de bruker KAIA. Kodebasen bør reflektere det praktiske workflow.
