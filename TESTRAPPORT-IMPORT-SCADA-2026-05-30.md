# Importdiagnose — SCADA (trend/fine/alarm) + settlement etter maskinbytte

**Dato:** 2026-05-30
**Bakgrunn:** Etter flytting av appen til ny maskin + re-import mistenkes manglende SCADA-importer (trend, fine, alarm). «Tidligere var det meste frem til mai grønt.»
**Metode:** Appens egen data-status (`GET /api/v1/data-status/overdue`) + gjennomgang av `CSV Eksporter/quarantine/` (feilede importer med `.error.txt`/`.diag.json`) + kode i `HotFolderWatcher`.

---

## Konklusjon

Importene feiler av **tre uavhengige årsaker** — alle ligger som stacktrace i `CSV Eksporter/quarantine/`. Dette er ekte feil, ikke manglende filer. Re-importen etter maskinbyttet utløste alle tre.

| # | Feil | Rammer | Rotårsak |
|---|---|---|---|
| 1 | `PathTooLongException` | operlog + scada-trend (grodemfoss m.fl.) | Filnavnet vokser for hver re-import; flytting til `done/` sprenger Windows MAX_PATH (260 tegn) |
| 2 | `TaskCanceledException` (HttpClient timeout 300 s) | scada-fine (15-min), 7 anlegg | 17 MB-fila rekker ikke å importeres innen 5-minutters-timeouten |
| 3 | `RegexMatchTimeoutException` | settlement Vikeså des-2025 | Parser-regex henger (katastrofal backtracking) på enkelte filer |

---

## Hva appen selv sier mangler (data-status/overdue, 2026)

| Kilde | Status 2026 | Detalj |
|---|---|---|
| **settlement** | jan–apr OK | mai mangler (10 av 11), Vikeså des-2025 feiler (årsak 3) |
| **scada (trend/time)** | i hovedsak OK | men re-import av grodemfoss trend feilet (årsak 1) — gammel data står fortsatt |
| **scada-fine (15-min)** | **mangler** for lindland, logjen, øgreyfoss, vikeså | årsak 2 (timeout på multi-fila) |
| **operlog (alarm)** | **mangler for 10 av 11** (kun grodemfoss inne) | årsak 1 (PathTooLong ved flytting til done) |

I tillegg er det store hull bakover i 2025 (jun–des) for scada, scada-fine og operlog — disse var grønne før og forsvant i migreringen.

---

## Årsak 1 — `PathTooLongException` (filnavn-vekst)

### Bevis
`quarantine/2026-05-29/…operlog…2026-05-04T08-10-18-861Z.csv.error.txt`:

```
System.IO.PathTooLongException: The specified file name or path is too long…
   at System.IO.File.Move(...)
   at HotFolderWatcher.MoveToDoneAsync(... HotFolderWatcher.cs:line 373)
```

### Rotårsak (kode)
`HotFolderWatcher.MoveToDoneAsync` (linje 371–372) bygger done-navnet ved å **prependе** `{plantId}_{sourceKey}_{timestamp}_` foran `file.Name`. Men `file.Name` inneholder allerede alle tidligere prefikser fra forrige import-runde. For hver re-import vokser navnet:

```
grodemfoss_operlog_20260519T055408863_grodemfoss_operlog_20260519T052246480_grodemfoss_operlog_…
```

Til slutt overstiger stien 260 tegn og `File.Move` til `done/` kaster. `QuarantineAsync` (linje 391) gjør det riktig — kaller `BuildStampedName(string.Empty, file.Name)` som *stripper* gamle prefikser før kapping — men `MoveToDoneAsync` stripper ikke, den bare legger på.

### Fiks
I `MoveToDoneAsync`: strip akkumulerte prefikser fra `file.Name` før ny stamp legges på (samme mønster som karantene-stien bruker), og kapp total sti mot MAX_PATH. Da slutter navnene å vokse.

**Workaround nå:** gi de karantenerte filene korte navn (f.eks. `grodemfoss_operlog_2026.csv`) og legg dem tilbake i `CSV Eksporter/`-roten for ny import.

---

## Årsak 2 — HttpClient-timeout 300 s (stor scada-fine)

### Bevis
`quarantine/2026-05-29/…scada-fine…15min…095812_MASTER.csv.error.txt`:

```
TaskCanceledException: The request was canceled due to the configured
HttpClient.Timeout of 300 seconds elapsing.
   at HotFolderWatcher.RouteAndImportAsync(... line 357)
```

Fila er **17 MB** og dekker 7 anlegg (drivdal, lindland, haukland, honnefoss, logjen, grodemfoss, vikeså). `POST /api/v1/scada/multi-plant-fine` rekker ikke fullføre innen 300 s.

### Fiks
Øk timeouten på den navngitte HttpClient-en `HotFolderUpload` (f.eks. 15–20 min) for store fine-filer, og/eller importer fine-data strømmende/i batcher i stedet for én request. Vurder også Kestrel `KeepAliveTimeout`/request-timeout.

**Workaround nå:** del 15-min-fila i mindre biter (f.eks. per anlegg eller per uke) før den legges i hot-folderen. Den mindre fine-fila (772 K i `done/2026-05/`) gikk gjennom — det er størrelsen som er problemet.

---

## Årsak 3 — `RegexMatchTimeoutException` (parser henger)

### Bevis
`quarantine/2026-05-30/dataeksport_20260521103004.xlsx.diag.json` (Vikeså, 01.–31.12.2025):

```
"Klarte ikke åpne workbook: RegexMatchTimeoutException: The Regex engine
 has timed out while trying to match a pattern to an input string…
 excessive backtracking caused by nested quantifiers…"
```

### Fiks
Identifiser regex-en i settlement-parseren (sannsynlig plant-navn-/periode-uttrekk) som backtracker. Bytt til et lineært mønster eller anker det (`^…$`, fjern nested quantifiers). Den globale `RegexMatchTimeout` fanger det i dag, men da feiler importen i stedet for å fullføre.

**Workaround nå:** denne ene fila (Vikeså des-2025) må trolig importeres manuelt etter regex-fiks.

---

## Anbefalt rekkefølge

1. **Årsak 1 (filnavn/MAX_PATH)** — rammer flest importer (all operlog + trend ved re-import). Kodefiks i `MoveToDoneAsync`. Til da: kort filnavn + re-drop.
2. **Årsak 2 (timeout)** — øk `HotFolderUpload`-timeout. Til da: del store fine-filer.
3. **Mai-settlement** — flytt mai-multifila fra `duplicates/` til roten (se økonomirapporten).
4. **Årsak 3 (regex)** — lavest volum (én fil), men bør fikses så parseren ikke kan henge.

> Merk: Mange feilede importer kan ha *delvis* commitet før flytting/timeout feilet. Etter fiks bør du verifisere mot `data-status/matrix` at hver (anlegg × kilde × måned) er grønn, og rydde eventuelle dobbeltrader.
