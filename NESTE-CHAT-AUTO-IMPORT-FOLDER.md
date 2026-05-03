# Neste sesjon — Auto-import fra overvåket mappe

**Dato opprettet:** 2026-05-03
**Spec:** `docs/SPEC-AUTO-IMPORT-FOLDER.md`
**Estimat:** 2-3 dager
**Avhengighet:** Bør implementeres etter `SPEC-IMPORT-COMPLETENESS.md`

## Mål

Drifts-leder kan slippe alle import-filer i én mappe (`C:\Morten\00 Oppetid\imports\inbox\`) og appen oppdager filene automatisk, identifiserer hvilket anlegg og hvilken kilde de tilhører, ruter til riktig importør, og oppdaterer dashboardet.

Eliminerer behovet for å drag-drop én og én via webgrensesnittet.

## Mappestruktur

```
C:\Morten\00 Oppetid\imports\
├── inbox/           ← Slipp filer her
├── processing/      ← Pågående (kortvarig)
├── done/2026-05/    ← Vellykket (sortert per måned)
├── quarantine/      ← Feilet (krever manuell håndtering)
└── _watch.lock      ← Watcher-lås
```

## Detekjons-pipeline per fil

1. Vent til fil er stabil (ferdig kopiert) — 5 sek check
2. Beregn SHA256-hash, sjekk mot `data_imports.file_hash` for duplikat
3. Detect filtype: settlement.xlsx / scada.csv / operlog.csv / hydrogrid.json
4. Detect anlegg: filnavn-regex først, fallback content-sniff (SCADA tag-prefiks)
5. **Multi-plant-håndtering:** hvis SCADA-fila har > 1 prefiks med ≥ 20 % andel — splitt logisk, importér for begge plants
6. Rute til riktig importør (samme kode som drag-drop bruker)
7. Lyktes → `done/YYYY-MM/`. Feilet → `quarantine/YYYY-MM-DD/` med .error.txt
8. Skriv `data_imports`-rad → completeness-dashboard oppdateres automatisk

## Konfigurerbar plant-prefiks-mapping

Kritisk konfig i `appsettings.json` for å løse navnekollisjoner som "LIAVT" (Honnefoss) vs "LIAVATN" (Liavatn-kraftverket):

```json
{
  "HotFolder": {
    "PlantPrefixMap": {
      "DRIVDAL": "drivdal",
      "LINDLAND": "lindland",
      "HAUKLAND": "haukland",
      "HONNE": "honnefoss",
      "LIAVT": "honnefoss",
      "LIAVATN": "liavatnkraft",
      "REVSVT": "liavatnkraft",
      "NODLANDVT": "liavatnkraft",
      "STOKKURHØLEN": "liavatnkraft"
    }
  }
}
```

## Bekreftede design-valg

| Valg | Beslutning |
|---|---|
| Watcher-mekanisme | `FileSystemWatcher` i `BackgroundService` |
| Fil-stabilitets-sjekk | 5 sek uendret før prosessering starter |
| Idempotens | SHA256-hash mot `data_imports.file_hash` |
| Max samtidige importer | 2 (konfigurerbar) |
| Multi-plant-fil | Logisk splitt, fysisk én fil i `done/multiplant/` |
| App restart | Filer i `processing/` flyttes tilbake til `inbox/` ved oppstart |
| Drag-drop fortsatt tilgjengelig | Ja — fallback for ad-hoc imports |
| Audit-logg | `user_id = "hotfolder:<machine-name>"` |

## Implementasjons-rekkefølge

| Steg | Innhold | Estimat |
|---|---|---|
| 1 | `HotFolderOptions` + mappestruktur ved oppstart | 1 t |
| 2 | `FileTypeDetector` + tester | 2 t |
| 3 | `PlantDetector` + tester | 3 t |
| 4 | `ImportRouter` + integrasjon med eksisterende importere | 2-3 t |
| 5 | `HotFolderWatcher` BackgroundService | 3-4 t |
| 6 | API-endepunkter | 2 t |
| 7 | `/hot-folder`-side med live-oppdatering | 4-5 t |
| 8 | End-to-end-test med 5 filer | 1 t |

## Spørre-policy

- Hvis eksisterende importere ikke kan kalles programmatisk (bare via HTTP): rapporter behov for refaktorering
- Hvis multi-plant-deteksjon flagger en fil hvor brukeren forventet én plant: pause og rapporter, det kan indikere feil i `PlantPrefixMap`
- Hvis quarantine fyller seg opp under første kjøring: stopp watcher, rapporter mønster av feilede filer

## Verifikasjon

Følg "Verifikasjon"-seksjonen i spec-en. Kritiske sjekkpunkter:

1. Etter steg 5: drop én test-fil i `inbox/`, verifiser at den havner i `done/` innen 10 sek
2. Etter steg 7: åpne `/hot-folder`, drop 3 filer, verifiser live-oppdatering av kø og status
3. End-to-end multi-plant: drop Honnefoss SCADA-fil med Liavatn-data, verifiser at to `data_imports`-rader skrives (en per plant)

## Når ferdig

Skriv `OVERLEVERING-2026-MM-DD-AUTO-IMPORT.md` med:
- Bekreftelse at hot folder fungerer for alle 4 kildetyper
- Eksempel-output fra første batch-prosessering
- Skjermbilde av `/hot-folder`-siden i drift
- Eventuelle filer som krevde manuell oppstrøms-fix før de fikk gjenkjennelse

Foreslåtte oppfølginger:
- Cloud-basert drop zone (Azure Blob trigger eller OneDrive sync)
- Auto-trigging av Hydrogrid-sync etter vellykket settlement-import
- Roll-back-funksjonalitet hvis import gikk galt
