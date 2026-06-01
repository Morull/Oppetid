# Status-audit — handover-kø 2026-05-19 til 2026-05-22

**Dato:** 2026-05-22
**Omfang:** Alle NESTE-CHAT / FORBEDRINGSFORSLAG / ANBEFALING-dokumenter laget etter siste formelle OVERLEVERING (6. mai), krysset mot live-app på `localhost:5180`.
**Metode:** Lest hver instruks for å trekke ut akseptkriteriene, deretter testet hvert punkt mot kjørende UI/API. Funn merket [Verifisert live], [Indirekte verifisert], [Ikke verifisert — krever DB/kode-sjekk] eller [Ikke startet].

---

## Sammendrag

| Status | Antall | Punkter |
|---|---|---|
| ✅ Levert (verifisert live) | 5 | KAIA-kostnad · Effektivitet 15-min · ApexCharts-krasj · Vakt-ROI Del B · Nedetid hendelsesdetaljer |
| ⚠️ Levert med åpne underpunkter | 2 | Normal årsproduksjon · FORBEDRINGSFORSLAG-EFFEKTIVITET |
| ❌ Ikke implementert | 4 | CR-merverdi-opprydding · Info-popup & sortering · KPI-kort-integritet · Tapsregnskap |
| 🔎 Krever DB-/kodesjekk | 1 | Øgreyfoss overløp-fix |
| 🐛 Eldre, åpne UI-bugs | 2 | Periodevelger «Egendefinert smitter» · Effektivitet-endepunkt tidsavbrudd |

7 av 11 vesentlige punkter er levert. 4 hovedfunksjonelle oppgaver står åpne, alle dokumentert med spec klar til implementasjon.

---

## ✅ Levert (verifisert live)

### 1. NESTE-CHAT-KAIA-KOSTNAD + FIKS-KAIA Del A
**Spec:** `docs/SPEC-KAIA-KOSTNAD.md` · Bygget skulle reddes, KAIA-feltene legges tilbake, committes.
**Verifisert:** Rapport-detalj viser KPI-kort «KAIA-kostnad 1 198,64 kr» med komponenter «Megler 869,87 kr · Fast 328,77 kr». 869,87 + 328,77 = 1 198,64 ✓. Bygget kjører.
**Status:** Levert.

### 2. NESTE-CHAT-EFFEKTIVITET-15MIN
**Spec:** 15-min SCADA-import, skille Genuine drift fra Start/stopp, drilldown.
**Verifisert:** Tabellen «Produksjons-intervaller (2394)» har 5 sorterbare kolonner inkl. Klassifisering. Klassifiseringer «Normal drift» og «Start/stopp» kommer fram i η(P)-kurvens legend. Tapstoppliste-tabellene (per-episode og per effekt-bånd) er bygget. Effekt-bins-tabell er der. Anleggssammenligning-seksjon eksisterer (Del 4.1 fra FORBEDRINGSFORSLAG-EFFEKTIVITET).
**Status:** Levert. UTGÅTT-erklæringen i `NESTE-CHAT-EFFEKTIVITET-TIMEDETALJER.md` stemmer.

### 3. ApexCharts NullReferenceException på Effektivitet (TESTRAPPORT funn #2)
**Verifisert:** Re-testet i dag — feilbanneret er borte, ingen console-exception ved init eller Dispose. Krasjet ligger ikke lenger.
**Status:** Levert.

### 4. NESTE-CHAT-FIKS-KAIA-OG-VAKTROI Del B (Vakt-ROI)
**Akseptkriterier dekket via Vakt-ROI hendelsestabell-kolonner:**

| Kolonne i tabellen | Tilsvarende krav |
|---|---|
| **Faktisk (t)** | B1 — `ActualEndOverrideUtc` (varighet kan overstyres per hendelse) |
| **Overløp (NOK)** | B2 — splittet overløps-del |
| **Ubalanse (NOK)** | B2 — splittet ubalanse-del |
| **Reddet (NOK)** | B2 — beholdt som totalsum |
| **Detaljer** | B3 — per-hendelse popup (kolonne tilstede; rad-knapp ikke verifisert i denne perioden fordi 0 events) |

**Forbehold:** B3-popupen ble ikke åpnet (ingen events i testperioden for ogreyfoss). Verifiser at Detaljer-knappen faktisk åpner dialog med full økonomi-breakdown — bør tas i en periode med events.
**Status:** Levert (B3 popup-innhold ikke åpnet i test).

### 5. NESTE-CHAT-NEDETID-HENDELSESDETALJER
**Verifisert:** Nedetid «Hendelser»-tabell har kolonnen **«Detaljer»**. Søster-funksjonen til Vakt-ROI B3 er på plass.
**Status:** Levert (knappens innhold ikke åpnet — samme begrensning som B3).

---

## ⚠️ Levert med åpne underpunkter

### 6. NESTE-CHAT-NORMAL-AARSPRODUKSJON
**Krav:** `NormalAarsproduksjonGwh` på `PlantRegistration`, vises og lagres i PlantAdmin.
**Verifisert indirekte:** Vakt-ROI viser nå «Anleggets andel av vaktkost 31 579 NOK/år · 8,8 % av total — Fordelt etter normal årsproduksjon (GWh-andel)». Feltet er ikke bare lagt til, det er **også koblet inn i Vakt-ROI-fordelingen** — som var listet som «senere/oppfølging» i instruksen.
**Status:** Feltet er levert + GWh-fordelt vaktkost-bruksområdet er levert. «Produksjon vs normalår»-indikatoren (det andre senere-punktet) er ikke verifisert.

### 7. FORBEDRINGSFORSLAG-EFFEKTIVITET (21. mai)
**Levert:** Underytelse-tapstoppliste (Del 3.2), per-effekt-bånd-gruppering (Del 3.3), Anleggssammenligning (Del 4.1), KPI-kort-seksjonstitler (Del 5: Effektivitet/Økonomi/Underytelse).
**Åpne underpunkter:**
- Del 2 feil B — η(P)-kurvens x-akse med ~5966 etiketter: ikke målt på nytt i dag. X-aksen rendrer som «Effekt (kW)» (riktig tittel), men antall etiketter ikke verifisert.
- Engelsk sjargong («Genuine», «Transition») — så «Klassifisering»-kolonne; faktiske verdier nå «Normal drift» og «Start/stopp» (norske). Sannsynligvis fikset, ikke alle anlegg verifisert.
- Dev-referanse «Spec NESTE-CHAT-EFFEKTIVITET-15MIN» i undertittel: ikke synlig i dagens DOM-tekst — trolig fjernet.

---

## ❌ Ikke implementert (live verifisert)

### 8. NESTE-CHAT-CR-MERVERDI-OPPRYDDING
**Krav:** Bytt formel for CR til `Σ(Elhub×spot)/Σ(Elhub)`, omdøp dagens «Merverdi vs spot» til «Realisert vs spot», innfør nytt «Timing-merverdi», invariant CR>1 ⟺ Timing-merverdi>0.
**Live på Capture rate-fanen i dag:**
- KPI-kort viser fortsatt **«Merverdi vs spot −81 183 NOK»** (gammelt navn).
- Tabellkolonne fortsatt **«Merverdi»**.
- Ingen forekomst av «Timing-merverdi» eller «Realisert vs spot» i DOM-en.

**Status:** Ikke startet.

### 9. FORBEDRINGSFORSLAG-INFOPOPUP-OG-SORTERING (i dag)
**Krav:** Info-popup på alle tabeller og grafer, sortering på alle kolonner.
**Status:** Skrevet i morges. Ikke implementert ennå (forventet).

### 10. FORBEDRINGSFORSLAG-KPI-KORT-INTEGRITET (i dag)
**Krav:** Byggekrav for KPI-kort + retting av Oppgjør-bruddet på Rapport-detalj.
**Status:** Skrevet i ettermiddag. Ikke rettet ennå (forventet). Oppgjør går fortsatt ikke opp.

### 11. ANBEFALING-TAPSREGNSKAP (i dag, ikke av meg)
**Status per dokumentet selv:** «Konsept avklart med bruker. Klar til implementasjon i prioritert rekkefølge.» Punkt 1 («Netto mot plan» på Produksjon-fanen) er anbefalt startpunkt. Ikke startet.

---

## 🔎 Krever DB-/kodesjekk

### 12. NESTE-CHAT-OGREYFOSS-OVERLOP-FIX
**Krav:** Endre `DalanePortfolioSignalMapSeeder.MapTag` til å sette `damId = "ogreyfoss_ogreyvatn"` for ogreyfoss, oppdater test, kjør data-migrering.
**Verifisert:** Nedetid-API for ogreyfoss april 2026 returnerer 11 events, alle kategori «TripFeil» — ingen med kategori «TettInntaksrist» og ingen `harOverlop=true`. Det betyr **enten** at det ikke var faktisk overløp i april (plausibelt), **eller** at fiksen ikke er på plass. Kan ikke skilles fra UI alene.
**Anbefaling:** Verifiser på en av disse måtene:
1. SQL: `SELECT signal_id, dam_id FROM core.signal_map WHERE plant_id='ogreyfoss' AND role='OverflowFlow';` — forventet `dam_id='ogreyfoss_ogreyvatn'`.
2. Sjekk `DalanePortfolioSignalMapSeeder.cs:64` — har den `if (plantId == "ogreyfoss") damId = "ogreyfoss_ogreyvatn";`?
3. Test på et tidsrom med kjent overløp (drifts-leder vet).

---

## 🐛 Eldre, åpne UI-bugs

### 13. Periodevelger «Egendefinert smitter» (TESTRAPPORT funn #4, UI-punkt 2.10)
**Verifisert i dag:** Fortsatt der. Periodevelgeren ble stående i «Egendefinert» med pilene døde, og pixel-klikk på «Måned»-knappen registrerte ikke før jeg dispatchet click via JS. Smitter mellom sider.

### 14. Effektivitet-endepunkt tidsavbrudd (ny observasjon)
**Symptom:** `/api/v1/plants/{plant}/effektivitet`-fetchen treffer HTTP client timeout (`net_http_request_timedout`) gjentatte ganger. Lastet til slutt etter ~36 s ved 3. forsøk. Ingen kodesesjon aktiv. Sannsynligvis genuint tregt endepunkt.
**Anbefaling:** Egen sak — sjekk om endepunktet kan optimaliseres (indeks, pre-aggregering) eller om timeoutet kan økes.

---

## Forslag til byggerekkefølge for utestående punkter

1. **Øgreyfoss overløp-fix** — verifiseres mot DB/kode først; trolig 30 minutters jobb hvis ikke gjort, klart definert.
2. **CR-merverdi-opprydding** — klart spec, kjøres som egen økt.
3. **Tapsregnskap punkt 1** («Netto mot plan» på Produksjon-fanen) — høyest verdi, lavest risiko per anbefalingen.
4. **KPI-kort-integritet** — rett Oppgjør-bruddet i samme slengen som Tapsregnskap-bygget, siden begge berører KPI-kort.
5. **Info-popup & sortering** — fellesarbeid (`Begreper.cs`, `KolonneHode`, `HjelpeIkon`) først, deretter utrulling.
6. **Effektivitet-endepunkt tidsavbrudd** — egen perf-sak.
7. **Periodevelger-bugen** — kosmetisk-funksjonell, men irritabel; lav prioritet.
8. **Tapsregnskap punkt 2–5** — etter at fundamentet står.

Punkt 1–3 er korte, klare og høy-verdi. 4–5 er parallelliserbare med 3.
