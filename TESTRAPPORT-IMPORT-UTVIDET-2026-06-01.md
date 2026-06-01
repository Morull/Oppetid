# Utvidet test av import-funksjonene + forbedringsforslag

**Dato:** 2026-06-01
**Bygger på:** `TESTRAPPORT-IMPORT-SCADA-2026-05-30.md`, `FIKS-IMPORT-OG-OKONOMI-2026-05-30.md`
**Metode:** Rotårsak-fiks i kode + gjennomgang av hele import-pipelinen (detektor, watcher, dedup, routing, completeness) + live-verifisering mot kjørende app (`/api/v1/data-status/*`, `/economy`).

---

## 1. Rotårsak rettet — «kommer fortsatt i duplicates» (Fiks 5)

### Funn (bevist live 2026-06-01)
Mai SCADA-trend-fila (`export-44-tags-avg-hour-20260601-090232`, hele mai, 11 anlegg) ble avvist til `duplicates/` ved hvert forsøk. `.dup.txt`-markøren viste:

> Hash: 2a544056… · Først sett: 09:05:16 · Kilde: scada

Den aller første kopien registrerte innholds-hashen, men dataene kom **aldri** i databasen (matrisen viser 0 celler for mai scada-trend). Alle senere forsøk spratt til `duplicates/`.

### Rotårsak (kode)
`HotFolderWatcher.ProcessFileAsync` registrerte hashen (`_dedup.TryRegister`) **før** importen ble kjørt. Når importen feilet (eller ikke fullførte), ble hashen liggende i cachen — i minnet på kjørende app. Da blir filen **permanent låst som duplikat** og kan aldri re-importeres uten full restart + manuell filkirurgi.

### Fiks
Delt sjekk fra registrering:
- Ny `HotFolderDedupCache.Contains(hash, out existing)` — peeker uten å registrere.
- `ProcessFileAsync` peeker nå før import, og kaller `TryRegister` **først etter vellykket import**.

Effekt: en fil som feiler havner i `quarantine/` (med feilmelding) og kan re-importeres etterpå. Kun faktisk vellykkede importer låser hashen. Dekket av 3 nye enhetstester i `HotFolderDedupCacheTests`.

> **Krever rebuild for å bli aktiv.** Inntil da: last fila rett inn forbi hot-folderen:
> `curl.exe -F "file=@<sti>;type=text/csv" "https://desktop-r4rfc53…ts.net/api/v1/scada/multi-plant"`

---

## 2. Utvidet test — status per importfunksjon

Verifisert mot kjørende app + kildedata.

| Funksjon | Status | Bevis / merknad |
|---|---|---|
| Settlement-import | ✅ Virker | Jan–apr 2026 + 2025-backfill importert, `coveragePct: 1`. Recent-imports viser dusinvis kl. 08–09 i dag. |
| Økonomi-aggregering over importer | ✅ Rettet | Vikeså ÅTD = 6 715 141 (var 6 904 492). Overlapp-dobbelttelling borte. |
| scada-fine (15-min) | ⚠️ Delvis | Mai dekket t.o.m. 21.05 (65 %). Importeres nå (timeout-fiks), men eksporten dekker ikke hele måneden. |
| scada-trend (time) | ❌→🔧 | Mai manglet helt (0 celler). Blokkert av register-før-import. Rettet i kode (Fiks 5), må rebuildes/lastes manuelt. |
| operlog (alarm) | ❌ Delvis data finnes | Multi-stasjons-fil finnes (t.o.m. mars 2026), men mye forventet operlog finnes ikke som logg. Se forslag 1. |
| Dedup ved re-drop | 🔧 Rettet | Register-etter-import (Fiks 5). |
| Filnavn-vekst / PathTooLong | ✅ Rettet | `HotFolderNaming` stripper+kapper (Fiks 1). |
| Store filer (timeout) | ✅ Rettet | Upload-timeout 5→30 min (Fiks 2). |
| Parser-regex henger | ✅ Rettet | Bundet kvantor (Fiks 3). |

### Hovedkonklusjon
Selve import-**motoren** virker nå (settlement strømmer inn, store filer og lange filnavn håndteres). De gjenstående hullene skyldes tre ting: (a) register-før-import-låsing — nå rettet, (b) eksporter som ikke dekker hele perioden (scada-fine/settlement t.o.m. 17.–21. mai), og (c) at appen forventer data som ikke finnes (operlog for alle anlegg).

---

## 3. Forbedringsforslag (prioritert)

### Forslag 1 — «Forventet» import må reflektere virkeligheten *(høy nytte, lav risiko)*
`DataSourceExpectations`-tabellen forventer alle kilder for alle 11 anlegg hver måned tilbake til 2025-06. Anlegg uten alarm-/SCADA-logg vil derfor **alltid** vises som «manglende», uansett hvor mye som importeres. Det skjuler ekte hull i støy.
**Tiltak:** per-(anlegg, kilde) «forventet-fra»-dato og mulighet for «ikke aktuelt» (N/A). Da blir rød celle = faktisk manglende data, ikke feilkonfigurert forventning.

### Forslag 2 — «Force reimport» / nullstill dedup for én fil fra UI *(høy nytte)*
Hele denne feilsøkingen var smertefull fordi det ikke finnes noen måte å tvinge re-import av én fil uten å stoppe appen, slette `.hotfolder-dedup.json` og flytte filer manuelt. 
**Tiltak:** admin-endepunkt + knapp «Importer på nytt» som (a) fjerner filens hash fra dedup-cachen (i minnet + disk) og (b) flytter fila fra `duplicates/`/`quarantine/` til roten. Eliminerer manuell filkirurgi.

### Forslag 3 — Nyere eksport skal *erstatte* eldre for samme (anlegg, kilde, periode) *(middels)*
I dag sameksisterer «mai 1–17» og «mai 1–20» (ga dobbelttelling, rettet i query-laget), og en rikere SCADA-eksport avvises som duplikat. Dette burde løses ved **import**, ikke bare ved spørring.
**Tiltak:** ved import, hvis en ny fil dekker ≥ perioden til en eksisterende import for samme (anlegg, kilde) → marker den gamle som superseded. Gjør `OverlappingImportResolver`-logikken til import-tids-semantikk.

### Forslag 4 — Felles innholds-hash-idempotens i import-laget *(middels)*
Hot-folder og manuell opplasting bruker ulik dedup-logikk. En fil som feiler i hot-folderen blokkeres, men slipper gjennom manuelt (eller omvendt).
**Tiltak:** flytt idempotensen til import-endepunktet (DB: `(file_hash, plant_id, source, period)`), slik at begge veier oppfører seg likt og en feilet import aldri blokkerer retry.

### Forslag 5 — Synliggjør watcher-beslutninger i UI *(lav-middels)*
`.dup.txt` / `.error.txt` / `.diag.json` er utmerket diagnostikk, men ligger begravd på disk.
**Tiltak:** vis siste N hot-folder-hendelser i UI (OK/DUPLIKAT/QUARANTINE + årsak + fil), med lenke til forslag 2-knappen.

### Forslag 6 — Del opp / strøm store SCADA-eksporter *(lav)*
17 MB+ multi-anleggs-fine-filer er skjøre (traff 300 s-timeouten). Selv med 30 min er det en risiko ved full historikk.
**Tiltak:** del server-side per anlegg/uke, eller strøm radvis i stedet for én multipart-request.

---

## Status på fiksene

| # | Fiks | Status |
|---|---|---|
| 1 | PathTooLong (filnavn) | I arbeidstreet, krever commit+rebuild |
| 2 | Upload-timeout 30 min | Live (bekreftet: scada-fine importeres) |
| 3 | Regex-backtracking | I arbeidstreet |
| 4 | Overlapp-dedup økonomi/KAIA | Live (bekreftet: Vikeså 6 715 141) |
| 5 | Register-etter-import | **Ny** — krever rebuild |

**Neste steg:** commit alle endringer, `docker compose … up --build -d`, deretter manuell opplasting av mai scada-trend-fila (eller la den nye dedup-logikken plukke den).
