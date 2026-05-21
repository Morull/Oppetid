# Instruks til Claude Code — 15-min effektivitetsdata + skille ekte drift fra start/stopp

**Denne instruksen erstatter `NESTE-CHAT-EFFEKTIVITET-TIMEDETALJER.md`** — den
gamle dekket bare drilldown-UI på hourly data; denne dekker hele jobben.

Kjør etter at de andre køede instruksene er committet (rører `Effektivitet.razor`
og SCADA-import-pipelinen).

---

## Bakgrunn

Effektivitet-siden viser i dag falske lave virkningsgrader (f.eks. «13 timer på
5,7 %» for Haukland). Årsak: SCADA-dataene er timesmidlet, og en klokketime som
inneholder en start/stopp drar både effekt og virkningsgrad mot null — et
gjennomsnitt, ikke et driftspunkt. Se forklaring i samtalen; kort sagt: en
turbin som kjørte 2 min av en time får time-snitt-η på et par prosent.

Løsningen er todelt: (1) importere SCADA-data med finere oppløsning, og (2) la
analysen skille **ekte produksjonsintervaller** fra **start/stopp-overganger**.

Drifts-leders krav, ordrett: vi må fange opp genuine sammenhengende perioder på
lav last/lav virkningsgrad (reell dårlig drift), men 0→produksjon-rampen i seg
selv skal ikke telle som et driftspunkt.

## Datagrunnlaget — finnes allerede

Det er produsert en ny SCADA-eksport: **30 tags (effekt + virkningsgrad +
vannføring for hver generator), 15-minutters snitt**. Eksempelfil ligger i
prosjektroten: `eksempeldata-effektivitet-15min.csv` (~5,8 MB, des 2025–apr
2026, 11 516 rader). Samme brede MASTER-format som dagens timesmaster
(`DateTime;Value (Cluster1.TAG);Unit (...);…`, semikolon, komma-desimal, BOM),
men 15-min spacing og hver rad er full (alle 30 tags har verdi hvert intervall).

De 30 taggene er: `*_GEN_P_PV`, `*_TURB_VIRKNGRD_PV`, `*_TURB_VF_PV` for
Drivdal G1, Grødemfoss G2, Haukland G1, Honnefoss G1, Lindland G1+G2, Løgjen G1,
Vikeså G1, Øgreyfoss (OGREY1 G1 + OGREY2 G2). Alle finnes i signal-mappingen fra
før.

---

## Del 1 — Importer 15-min-eksporten UTEN å kollidere med hourly

**Kritisk fallgruve.** SCADA-samples lagres i `core.sample_facts` med
primærnøkkel `(asset_id, signal_id, time_utc)` — det finnes *ikke* noe
oppløsnings-begrep (`ScadaSample`-recorden har bare AssetId, SignalId, TimeUtc,
Value, Quality). Effektivitets-taggene finnes allerede i den hourly 73-tag-
masteren. 15-min-eksporten har samme tags og treffer samme `:00`-tidsstempler —
så en naiv import ville **overskrevet de hourly verdiene** for disse taggene.
Det ville ødelagt andre konsumenter (f.eks. `OverflowQueryService`s
ProductionStateProxy som leser GeneratorActivePower hourly).

**Løsning — lagre de finkornede dataene adskilt.** Anbefalt: en egen tabell
(f.eks. `core.sample_facts_fine`, samme kolonner som `sample_facts`) med eget
repository, slik at den hourly pipelinen står helt urørt. Et alternativ er å
legge `resolution_minutes` inn i primærnøkkelen på `sample_facts` — men det
berører entiteten, repo-interfacet og alle konsumenter, så egen tabell er
lavere risiko. Velg egen tabell med mindre du ser en god grunn til noe annet —
er du i tvil, **stopp og spør Morten**.

Konkret:
- Ny tabell + skjema-bro (idempotent, samme mønster som ellers), eller Timescale
  hypertable hvis `sample_facts` er det.
- Nytt repository for finkornede samples (speil `IScadaSampleRepository`:
  `BulkInsertAsync` + `ListAsync`).
- **Ruting:** `HotFolderDetector` / import-rutingen må kjenne igjen 15-min-
  effektivitetseksporten — på filnavn-markøren `avg-15min` og/eller 30-tag-
  kolonnesettet — og sende den til den nye import-veien. Den må **ikke** havne i
  den vanlige SCADA-importen (som skriver til `sample_facts`).
- Selve parsingen kan gjenbruke `ScadaMasterCsvParser` — formatet er identisk;
  det er kun lagrings-destinasjonen som er ny.
- Test-fil: bruk `eksempeldata-effektivitet-15min.csv` i prosjektroten. **Ikke
  legg den i `CSV Eksporter`/hot-folder før import-koden er klar** — da plukkes
  den opp av dagens importer og havner feil.

## Del 2 — Effektivitet-analysen (`EffektivitetQueryService`)

Bytt datakilden til det nye 15-min-repositoryet, og gjør disse endringene:

1. **Integrasjonskonstantene må rettes.** I dag antar koden 1-times samples:
   `sumP_kWh += vals.P.Value` (kW × 1 t) og `sumQ_m3 += vals.Q.Value * 3600`.
   Med 15-min intervaller er energi per sample `P_kW × 0,25 t` og volum
   `Q_m³/s × 900 s`. Rett begge — ellers blir TotalProduksjonKwh og spesifikt
   vannforbruk 4× feil.
2. **`ProduksjonsTimer`** blir nå et intervall-antall. Konverter til timer der
   en varighet er ment (÷ 4), eller gi feltet/etiketten et riktig navn.
3. **Klassifiser hvert intervall** (P, η, Q for ett 15-min steg):
   - *Stoppet:* P under produksjonsterskel → ekskluder (som i dag).
   - *Start/stopp-overgang:* P over terskel, men η under et fysisk troverdig
     gulv → flagg som overgang. En turbin i jevn drift, selv på dyp dellast,
     ligger ikke under ~50 % virkningsgrad; en lavere 15-min-η betyr at
     intervallet var delvis stoppet. Legg gulvet som en navngitt, lett
     justerbar konstant (start på ~50 %).
   - *Ekte produksjonsintervall:* P over terskel og η over gulvet.
4. **Bruk kun ekte produksjonsintervaller** til `SnittEtaPct`, sweet-spot,
   bins og η(P)-kurven. Da forsvinner den falske 5,7 %-bin'en.
5. **Behold overgangs-intervallene** i `Punkter` med et klassifiserings-felt
   (utvid `EffektivitetPunkt` med en enum, f.eks. `Genuine` / `Transition`) —
   de skal ikke forsvinne, de skal vises merket i drilldown (Del 3).

Designmålet, for å være tydelig: en genuin ~40-min lavlast-kjøring gir 2–3
etterfølgende *ekte* 15-min-intervaller på reell (om enn redusert) η — fanget
opp. En ren 0→produksjon-rampe gir ett isolert *overgangs*-intervall — flagget
ut av statistikken, men fortsatt synlig. Oppdater også klasse-kommentaren som i
dag sier «SCADA-aggregat er allerede time-aligned … per klokketime».

## Del 3 — Effektivitet-UI (`Effektivitet.razor`)

Dette er drilldown-en fra den erstattede instruksen, nå med klassifisering:

- **Ny tabell «Produksjons-intervaller»** bygget på `_response.Punkter`,
  virtualisert `MudTable` (mønster fra `Nedetid.razor`). Kolonner: Tidspunkt
  (lokal tid), Effekt (kW), Virkningsgrad (%), Vannføring (m³/s), og
  **Klassifisering** (Ekte / Start-stopp). Sorterbar, søkbar.
- **Effekt-bin-tabellen blir drilldown:** klikk på en bin filtrerer
  intervall-tabellen til det effekt-intervallet og scroller dit.
- **Scatter-tooltip** viser tidspunkt; marker gjerne overgangs-punkter visuelt
  forskjellig fra ekte punkter.
- **CSV-eksport** av de viste intervallene (Tidspunkt, Effekt, η, Vannføring,
  Klassifisering) — generér klientside fra `Punkter`, unngå nytt API-endepunkt.

## Akseptkriterier

- [ ] `dotnet build` grønt; effektivitets-tester oppdatert/grønne.
- [ ] 15-min-eksporten importeres til egen lagring; `core.sample_facts` og
      hourly-konsumenter er fullstendig uberørt.
- [ ] `eksempeldata-effektivitet-15min.csv` importerer uten feil.
- [ ] TotalProduksjonKwh og spesifikt vannforbruk er korrekte (ikke 4× feil).
- [ ] «Snitt η», sweet-spot og η(P)-kurven bygger kun på ekte
      produksjonsintervaller — den falske ~5,7 %-bin'en er borte.
- [ ] Start/stopp-overganger er fortsatt synlige i intervall-tabellen, merket.
- [ ] Verifiser mot Haukland: en genuin lavlast-kjøring fanges som ekte
      intervaller; rene 0→produksjon-overganger er flagget, ikke talt med.
- [ ] Drilldown: klikk på en bin viser intervallene i den.

## Merknad

Den finkornede importen i Del 1 legger også grunnlaget for senere bruk
(skarpere nedetid-tidfesting, Vakt-ROI, en start/stopp-frekvens-KPI) — men
det er egne, senere instrukser. Hold denne avgrenset til import + Effektivitet.
