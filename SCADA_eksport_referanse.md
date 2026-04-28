# SCADA-eksport – referanse for programmering

Dette dokumentet beskriver hva som kan eksporteres fra SCADA-systemet til CSV. Lim inn i nye prosjekter for å gi Claude kontekst.

## SCADA-plattform

- **System:** Citect SCADA (Schneider Electric / AVEVA Plant SCADA)
- **Cluster:** `Cluster1` (Citect cluster server-arkitektur)
- **Eksport:** Manuell pr. i dag (ikke automatisert via API/CLI)
- **Kvalitetsflagg (Good/Bad/Uncertain):** Ikke tilgjengelig i eksport – tomme verdier representeres som blank/NaN
- **Tag-historikk kan eksporteres som rådata** (uaggregert) i tillegg til aggregerte oppløsninger
- **Operatør-/alarmlogg er hendelsesdrevet** – logger kun når en hendelse faktisk inntreffer (ingen aggregering)

## Generelt

- **Eksportformat:** CSV
- **Skilletegn:** Semikolon (`;`)
- **Desimaltegn:** Komma (`,`) – må håndteres ved parsing (f.eks. `pandas.read_csv(..., sep=";", decimal=",")`)
- **Tegnsett:** Norsk (Æ, Ø, Å forekommer i stasjonsnavn) – les som UTF-8, fall tilbake til CP1252 om nødvendig
- **To hovedtyper eksport:**
  1. Tag-historikk (tidsserier av prosessverdier)
  2. Operatør- og alarmlogg (hendelser)

## 1. Tag-historikk (tidsserier)

**Eksempel filnavn:** `export-66-tags-avg-hour-20260428-151414_MASTER.csv`

Filnavnskonvensjon: `export-<antall>-tags-<aggregering>-<oppløsning>-<YYYYMMDD>-<HHMMSS>_MASTER.csv`

### Aggregeringstyper (sett i filnavn)
- `avg` – gjennomsnitt
- Andre standard Citect-aggregater: `min`, `max`, `sum`, `last`, `raw` (uaggregert / rådata)

### Oppløsninger
- `hour`, `minute`, `day` – og kortere/lengre etter behov
- Rådata (uaggregert, lagringsfrekvens fra Citect Trend) er mulig

### Filstruktur
- **Kolonne 1:** `DateTime` – format `YYYY-MM-DD HH:MM:SS.mmm` (lokal tid, ikke UTC)
- **Etterfølgende kolonner:** Alternerende `Value (<tag>)` og `Unit (<tag>)` for hver tag
- Antall kolonner = `1 + 2 × antall_tagger`
- Tomme verdier kan forekomme; enhet kan være `None` for digitale/alarm-tagger

### Tag-navnekonvensjon
`STASJON_AGGREGAT_SYSTEM_KOMPONENT_MÅLING_TYPE`

Eksempel: `Cluster1.ORSDAL_G1_GEN_VIKLING_L2_I_PV`
- `Cluster1` = SCADA-cluster
- `ORSDAL` = stasjon
- `G1` = aggregat / generator 1
- `GEN` = subsystem (generator)
- `VIKLING_L2_I` = komponent + målestørrelse (vikling fase L2, strøm)
- `PV` = type (Process Value)

**Type-suffiks som forekommer:**
- `PV` – Process Value (måleverdi)
- `SP` – Setpoint (settpunkt)
- `AL` – Alarm
- `CMD` – Command
- `LAST` – sist mottatt verdi
- `TM` – målt verdi for settpunkt

### Kategorier av tagger som er tilgjengelige (eksempel fra Ørsdal G1)

| Kategori | Eksempler | Enheter |
|---|---|---|
| Generator vikling (strøm/spenning/temp) | `GEN_VIKLING_L1/L2/L3_I/U/TEMP` | A, kV, °C |
| Generator ytelse | `GEN_P/Q/S/COSPHI/F/TURTALL/PRODUKSJON/DRIFTSTIMER` | kW, kVAr, kVA, cos φ, Hz, rpm, kWh, h |
| Lager og vibrasjon | `GEN_AKSLAGER/RADLAGER_DE/NDE_TEMP/VIBRASJON` | °C, mm/s |
| Transformator | `TRAFO_OLJE_TEMP` | °C |
| Hydraulikk | `HYDRL_OLJE_TEMP/TRYKK` | °C, bar |
| Kjølevann | `KJOLEVANN_KALD/VARM_TEMP`, `TRYKK_FORAN_FILTER`, `VENTIL_POS` | °C, bar, % |
| Generator luft | `GEN_VARMLUFT/KALDLUFT_TEMP` | °C |
| Turbin (Pelton-aktig) | `TURB_VANN_TRYKK`, `RORGATE_VANN_TRYKK`, `TURB_DYSE1-4_APNING`, `TURB_DEFL_APNING`, `TURB_PADRAG` | mVs, % |
| Inntak/dam | `INNTAK_NIVA_OPPSTROM/NEDSTROM_KOTE`, `INNTAK_MINVF_LITER`, `INNTAK_RIST_FALLTAP`, `INNTAK_MET_LUFTTEMP_UTE` | moh, cm, l/s, °C |
| Nett/linje | `NETT_LINJE_FASE_L1-L3_U_*`, `NETT_LINJE_F` | kV, Hz |
| Kontroll/regulering | `KONTROLL_REG_NIVA/P/U_SP_*` | cm, kW, kV |

Alle stasjoner/aggregater følger samme mønster, men taggene varierer noe per kraftverk.

## 2. Operatør- og alarmlogg

**Eksempel filnavn:** `operlog-export-2026-04-28T13-16-36-881Z.csv`

Filnavnskonvensjon: `operlog-export-<ISO8601-UTC>.csv`

### Filstruktur (11 kolonner)

| Kolonne | Innhold | Eksempel |
|---|---|---|
| `timestamp` | ISO 8601 UTC (`Z`-suffiks) | `2026-04-28T13:05:46.000Z` |
| `station` | Stasjon | `Øgreyfoss` |
| `username` | Operatør, system eller domenekonto | `Øgreyfoss`, `AGC`, `KRAFTSCADA\navn.etternavn` |
| `tag` | Tag-id (samme konvensjon som over) | `OGREY1_G1_KONTROLL_STILLSTAND_AL` |
| `text` | Lesbar beskrivelse på norsk | `G1 stillstand` |
| `value` | Numerisk verdi (ofte tom) | `10,3` |
| `operatorType` | Tag-id på punktform (ofte tom) | `OGREY1.G1.KONTROLL.AGC` |
| `originTable` | `operatorlog` eller `alarmlog` | `alarmlog` |
| `alarmType` | `alarm`, `event` eller tom | `event` |
| `categoryNumber` | Tallkategori (typisk `3` for event, tom for operatorlog) | `3` |
| `offTimestamp` | Når alarm/hendelse deaktiveres (kan være tom) | `2026-04-28T13:01:09.000Z` |

### Hendelsestyper som logges
- **Driftsekvenser:** start/stopp, stillstand, tomgang, startsekvens klar, stoppsekvens pågår
- **Regulering:** `REG_P_AKTIV` (effekt), `REG_U_AKTIV` (spenning), `REG_NIVA_AKTIV` (nivå)
- **AGC:** settpunkt fra database (`AGC_DB_SP`), `AGC_STOPP_CMD`
- **Komponenter:** trafo-pumper, effektbrytere, ventiler
- **Operatørhandlinger:** innlogging, lokalt display, manuelle settpunkt

## Tilgjengelige stasjoner i SCADA-systemet

Stasjoner observert i datasettene:
- **ORSDAL** (Ørsdal kraftverk – Pelton, G1)
- **ØGREYFOSS** (`OGREY1`, `OGREY2` – G1 og G2)
- **URDALVT** (Urdalsvann – inntak/luke, koblet via AGC)
- **TEKSEVT** (Teksevatn – inntak/luke)
- Flere aggregater (`G1`, `G2`) per stasjon der relevant

**Andre stasjoner** har ~95 % tilsvarende tag-struktur og samme eksportmuligheter som ORSDAL/ØGREYFOSS. Anta at samme navngivning, kategoriinndeling og aggregeringer gjelder med mindre annet er oppgitt.

## Praktiske notater for parsing

- **Pandas:** `pd.read_csv(path, sep=";", decimal=",", parse_dates=["DateTime"])`
- **Tag-historikk har "wide" format** – ofte ønskelig å pivotere til "long" (DateTime, tag, value, unit) for analyser
- **Enheter står i hver annen kolonne** – kan trekkes ut som metadata til en `dict[tag] = unit`
- **Operlog timestamps er UTC**, mens tag-historikk er **lokal tid** (Europe/Oslo) – konverter til samme tidssone før kobling
- **`offTimestamp` lar deg beregne alarmvarighet:** `offTimestamp - timestamp`
- **Filter på `originTable`** for å skille operatørhandlinger fra alarmer/events
- **Aliaser i tag-navn:** Cluster-prefiks (`Cluster1.`) kan strippes; understrek vs punktum er forskjellig mellom kolonneoverskrift og `operatorType`
- **Kvalitetsflagg finnes ikke** – behandle blanke verdier som `NaN`. Manglende rader betyr ingen verdi ble logget (Citect deadband / event-basert lagring)

## Hva som typisk er ønskelig som leveranse

- Pivotering til long format (TimescaleDB / Parquet)
- KPI-rapporter (driftstid, produksjon, virkningsgrad, MTBF)
- Vibrasjons- og temperaturtrender for tilstandsovervåking
- Korrelering av alarmlogg mot prosessverdier (root cause)
- Eksport til Excel/Power BI med riktige enheter

---

## Begrensninger å være klar over

- Eksport er manuell – planlegg arbeidsflyt rundt periodiske CSV-dumper
- Ingen kvalitetsflagg – kan ikke skille mellom "ekte 0", "feil sensor" og "manglende verdi"
- Operlog er ren hendelseslogg – ingen aggregering eller status-snapshot
- Tidssoneblanding (lokal tid i tag-historikk vs. UTC i operlog) må håndteres eksplisitt
