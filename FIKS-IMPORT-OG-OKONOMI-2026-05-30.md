# Kodeendringer — import-feil + økonomi-dobbelttelling

**Dato:** 2026-05-30
**Bygger på:** `TESTRAPPORT-IMPORT-SCADA-2026-05-30.md` + `TESTRAPPORT-OKONOMI-2026-05-29.md`

Fire bekreftede feil er adressert. Alle endringer er gjort i arbeidstreet. **De trer ikke i kraft før containeren bygges på nytt:**

```
docker compose -f docker-compose.yml -f docker-compose.tailscale.yml up --build -d
```

---

## Endrede/nye filer

| Fil | Endring | Fikser |
|---|---|---|
| `src/KraftverkUptime.Infrastructure/Reporting/OverlappingImportResolver.cs` | **Ny** — kollapser overlappende importer | Fiks 4 |
| `src/KraftverkUptime.Infrastructure/Reporting/EconomyReportQueryService.cs` | Bruker resolver i stedet for tuppel-dedup | Fiks 4 |
| `src/KraftverkUptime.Infrastructure/Reporting/KaiaCostQueryService.cs` | Dedup per anlegg på overlapp | Fiks 4 |
| `src/KraftverkUptime.Infrastructure/InfrastructureServiceCollectionExtensions.cs` | Upload-timeout 5 → 30 min (konfigurerbar) | Fiks 2 |
| `src/KraftverkUptime.Modules.Settlement/Parsing/ExcelSettlementParser.cs` | Bundet regex mot backtracking | Fiks 3 |
| `src/KraftverkUptime.Infrastructure/HotFolder/HotFolderDetector.cs` | Bundet regex mot backtracking | Fiks 3 |
| `tests/KraftverkUptime.Infrastructure.Tests/OverlappingImportResolverTests.cs` | **Ny** — 4 regresjonstester | Fiks 4 |

---

## Fiks 1 — PathTooLong (filnavn-vekst)

**Allerede løst i arbeidstreet** (`HotFolderNaming.cs` + `MoveToDoneAsync`), men ucommittet. `BuildStampedName` stripper akkumulerte prefikser og kapper til 120 tegn. PathTooLong-feilene er datert 29.05; 30.05-kjøringen har ingen — fiksen er aktiv så snart den committes og bygges. **Ingen ny kodeendring nødvendig — bare commit + rebuild.**

## Fiks 2 — Upload-timeout

`HotFolderUpload`-klienten hadde 5 min timeout; 17 MB 15-min-fila brukte lengre tid og fikk `TaskCanceledException`. Nå 30 min (default), overstyrbar via `HotFolder:UploadTimeoutMinutes`. Vurder å dele svært store fine-filer uansett.

## Fiks 3 — Regex-backtracking

`PlantTitleRegex` (`^(?<name>.+?)\s+\d{1,2}\.\d{1,2}\.\d{4}`, 50 ms) backtracket på lange celleverdier og kastet `RegexMatchTimeoutException` → hele importen feilet. Byttet til bundet kvantor `^(?<name>.{1,80}?)\s{1,8}\d{1,2}\.\d{1,2}\.\d{4}` (lineær) + 250 ms margin. Endret i begge speilede kopier (parser + detektor). Matcher fortsatt alle gyldige titler.

## Fiks 4 — Overlapp-dobbelttelling (økonomi + KAIA)

`EconomyReportQueryService` og `KaiaCostQueryService` dedupliserte kun på **eksakt** lik `(PeriodStart, PeriodEnd)`. To overlappende importer av samme måned (Vikeså «mai 1–17» + «mai 1–20») havnet i hver sin gruppe og ble begge summert → ÅTD-spotomsetning 80 886 673 i stedet for 80 697 323 (+189 351, bekreftet live).

Ny `OverlappingImportResolver.ResolveNonOverlapping(...)` sorterer på start og slår sammen overlappende perioder; per klynge beholdes den bredeste importen (lengst varighet), tie-break nyeste `ImportedAtUtc`. Distinkte måneder beholdes uendret; ekte duplikater oppfører seg som før (nyeste vinner). Dekket av 4 enhetstester.

---

## Etter rebuild — verifiser

1. `GET /api/v1/economy?plants=all&kind=YearToDate` → Vikeså spotoms skal være ~6 715 141 (ikke 6 904 492), total ~80 697 323.
2. Legg den karantenerte 17 MB fine-fila tilbake i `CSV Eksporter/` → skal importeres uten timeout.
3. Legg mai-multifila fra `duplicates/2026-05/` tilbake i roten → mai blir grønt for de 9 anleggene.
4. `dotnet test tests/KraftverkUptime.Infrastructure.Tests` → de 4 nye testene skal passere.

> Merk: Fiks 4 fjerner dobbelttellingen i *beregningen*. Den utdaterte «mai 1–17»-importen bør likevel slettes fra DB for ryddighet (gjør du selv — jeg gjør ikke permanente slettinger).
