# Haukland — API-data vi trenger

Vi ønsker å hente data fra SCADA via API i stedet for CSV-eksport. Vil bare ha noen få tags, så trafikken skal være lav.

## 1. Trend-data (timeintervall)

7 tags. Time-snitt holder — samme som dagens `avg-hour`-CSV.

| Tag | Enhet | Beskrivelse |
|---|---|---|
| HAUKLAND_G1_GEN_P_PV | kW | Generator aktiv effekt |
| HAUKLAND_G1_TURB_VF_PV | m³/s | Turbin vannføring |
| HAUKLAND_G1_TURB_VIRKNGRD_PV | % | Turbin virkningsgrad |
| HAUKLAND_STEMMEVT_KONTROLL_MAG_OVLOP_PV | m³/s | Stemmevatn overløp |
| HAUKLAND_STEMMEVT_KONTROLL_MAG_VOLUM_PV | Mill.m³ | Stemmevatn volum |
| HAUKLAND_STEMMEVT_KONTROLL_TOT_VF_PV | m³/s | Stemmevatn total vannføring |
| HAUKLAND_STEMMEVT_KONTROLL_MAG_FYLLGRD_PV | % | Stemmevatn fyllgrad |

**Frekvens**: 1 gang/time
**Format ønskelig**: JSON, evt. CSV hvis enklere
**Tidssone**: UTC

### Eksempel på respons (JSON)

```json
[
  {
    "tag": "HAUKLAND_G1_GEN_P_PV",
    "timestamp": "2026-05-12T13:00:00Z",
    "value": 1428.5,
    "unit": "kW",
    "quality": "Good"
  },
  {
    "tag": "HAUKLAND_STEMMEVT_KONTROLL_MAG_FYLLGRD_PV",
    "timestamp": "2026-05-12T13:00:00Z",
    "value": 92.55,
    "unit": "%",
    "quality": "Good"
  }
]
```

### Eller CSV (samme som dagens eksport)

```
DateTime;Value (Cluster1.HAUKLAND_G1_GEN_P_PV);Unit (...);Value (Cluster1.HAUKLAND_STEMMEVT_KONTROLL_MAG_OVLOP_PV);Unit (...)
2026-05-12 13:00:00.000;1428.5;kW;0;m3/s
2026-05-12 14:00:00.000;1432.1;kW;0;m3/s
```

## 2. Alarmer / hendelser

Ønsker hele `alarmlog`-tabellen, men IKKE `operatorlog` (settpunkt-endringer trenger vi ikke).

**Frekvens**: når noe skjer (push), eller hver 5–10 min hvis polling
**Format**: samme som dagens operlog-CSV holder

### Eksempel-rad

```
timestamp;station;tag;text;alarmType;offTimestamp
2026-02-25T12:03:37.000Z;Haukland;HAUKLAND_G1_KONTROLL_STARTER_AL;G1 startsekvens pågår;event;2026-02-25T12:11:12.000Z
2026-02-25T11:47:26.000Z;Haukland;HAUKLAND_G1_TRAFO_DIFFVERN_KRITISK_AL;Trafo differensialvern kritisk;alarm;2026-02-25T11:56:18.000Z
```

Spesielt viktige tag-mønstre (klassifiserer drift/nedetid hos oss):
- `*_STARTER_AL`, `*_STOPPER_AL` — start/stopp
- `*NODSTOPP*`, `*HURTIGSTOPP*` — nødstopp
- `*FEIL_AL*`, `*HAVARI*`, `*TURB_FEIL*` — feil
- `*_LL_AL`, `*_HH_AL` — grenseverdialarmer

Men send gjerne hele alarmlog-tabellen — vi filtrerer selv på vår side.

## 3. Spørsmål til SCADA

- Er det mulig å eksponere disse via API? (REST eller annet)
- Hvilken autentisering (token, basic, sertifikat)?
- Er det noen begrensning på antall kall per minutt?
- Kan vi få historiske data tilbake i tid, eller bare fra "nå" og fremover?
- Hva er kostnaden / krever det utvidet lisens?

Hvis API ikke er aktuelt, fungerer dagens CSV-eksport også — vi vil bare slippe manuell håndtering og redusere antall tags.

## Kontekst

Vi bygger en uptime-/effektivitetsapp (KraftverkUptime). Trender brukes til virkningsgrad-beregning og vakt-ROI (overløp). Alarmlog brukes til klassifisering av nedetid. Vi trenger 30–60 min responsivitet — ingen sanntid.

Hvis dette fungerer for Haukland, ønsker vi tilsvarende oppsett for Drivdal, Grødemfoss, Honnefoss og Lindland (5–10 tags per anlegg).
