# Prompt: Oppetidsanalyse av kraftverk – modulær applikasjonsarkitektur

## Formål med prompten
Denne prompten brukes til å generere en produksjonsklar, modulært oppbygd applikasjon for oppetidsanalyse (availability/uptime analysis) av kraftverk. Arkitekturen skal være fremtidsrettet slik at nye moduler (f.eks. prediktivt vedlikehold, produksjonsprognoser, CO2-rapportering) kan legges til uten refaktorering av kjernen.

---

## Systemprompt (rolle)

Du er en senior softwarearkitekt og dataingeniør med spisskompetanse innen:
- Kraftbransjen (vann-, vind-, termisk og solkraft), inkludert driftskonsepter som tilgjengelighetsfaktor, kapasitetsfaktor, MTBF, MTTR, tvunget utkobling og planlagt utkobling i tråd med IEC 61400-26 og IEEE 762.
- Azure-plattformen (App Service, Functions, Container Apps, AKS, Key Vault, Managed Identity, Azure AD/Entra ID, API Management, Event Hubs, Data Lake Gen2, Synapse, Fabric, Application Insights).
- Modulær monorepo-arkitektur, Domain-Driven Design, hexagonal/clean architecture og plugin-basert utvidelse.
- Sikkerhet: Zero Trust, OWASP ASVS, secret management, RBAC, audit logging.

Svar alltid med konkret, kjørbar kode og begrunnede arkitekturvalg. Flagg fallgruver eksplisitt.

---

## Oppdrag

Bygg en applikasjon kalt `KraftverkUptime` som gjennomfører oppetidsanalyse basert på driftsdata fra ett eller flere kraftverk. Applikasjonen skal være modulbasert fra dag én slik at jeg senere kan plugge inn nye analysemoduler, datakilder og rapporteringsformater uten å endre kjernen.

### Kjernekrav

1. **Modulær arkitektur**
   - Bruk en plugin-/modulmodell (eksempel: .NET 8 med `IModule`-interface, eller Python med `entry_points`/`pluggy`).
   - Kjernen (core) skal kun definere kontrakter: `IDataSource`, `IAnalyzer`, `IReportSink`, `IAuthProvider`.
   - Moduler registreres via DI-container og konfigurasjonsfil (`modules.yaml` eller `appsettings.json`).
   - Første modul som implementeres: `UptimeAnalyzer` (beregner availability %, MTBF, MTTR, top-5 nedetidsårsaker).

2. **Datakilder (API-inntak)**
   - Abstraksjon `IDataSource` med metoder `FetchTimeSeries(assetId, from, to)` og `FetchEvents(...)`.
   - Implementer minst to adaptere:
     - Generisk REST-klient med OAuth2 Client Credentials / API-nøkkel / mTLS.
     - OPC UA-adapter (bruk `Opc.Ua.Client` eller `asyncua`) – kan være stub med tydelig TODO.
   - Retry med eksponentiell backoff (Polly / tenacity), sirkulær bryter, rate limiting og dead-letter queue.
   - Skjemavalidering av innkommende data (JSON Schema / Pydantic / FluentValidation).

3. **Azure-integrasjon (sikkerhet og drift)**
   - All autentisering via **Managed Identity** – ingen hemmeligheter i kode eller config.
   - Secrets og API-nøkler hentes fra **Azure Key Vault** via `DefaultAzureCredential`.
   - Logger og metrikker til **Application Insights** (OpenTelemetry).
   - Støtte for **Azure AD / Entra ID** for brukerautentisering (OIDC) og RBAC via app-roller.
   - Deployment som container til **Azure Container Apps** (foretrukket) eller App Service.
   - IaC med **Bicep** eller **Terraform** – inkluder minimum: RG, Key Vault, Container App, Log Analytics, Managed Identity, Storage, Event Hub.
   - CI/CD: GitHub Actions / Azure DevOps med OIDC federated credentials (ingen service principal-secrets).

4. **Sikkerhet (innebygd, ikke bolted on)**
   - Input-validering og output-encoding overalt.
   - Rolleseparasjon: `Reader`, `Analyst`, `Admin`.
   - Alle API-endepunkter bak Azure API Management med rate limiting og WAF.
   - Audit-logg av alle spørringer som henter kraftverksdata (skriv til immutable storage / Log Analytics).
   - Data-in-transit: TLS 1.2+. Data-at-rest: CMK via Key Vault.
   - Secret scanning (GitGuardian/Gitleaks) og SBOM-generering (CycloneDX) i pipeline.
   - Følg Microsoft Security Development Lifecycle (SDL) og CIS Azure Benchmark.

5. **Utvidbarhet (fremtidige moduler)**
   - Forhåndsdefiner kontrakter for kommende moduler slik at jeg bare trenger å implementere dem:
     - `PredictiveMaintenanceModule` (ML-modell inferens)
     - `ProductionForecastModule`
     - `RegulatoryReportModule` (NVE/Statnett-rapportering)
     - `AnomalyDetectionModule`
   - Alle moduler skal kunne konsumere samme datastrøm uten duplisering (pub/sub via Event Hub eller intern MediatR/message bus).

6. **Observability**
   - Structured logging (JSON) med korrelasjon-ID gjennom hele forespørselskjeden.
   - Helse- og readiness-endepunkter (`/health`, `/ready`).
   - Metrikker: request-latency, modul-eksekveringstid, feilrater per datakilde.

---

## Ønsket leveranse fra deg (AI-modellen)

Lever i denne rekkefølgen, og stopp etter hvert steg for bekreftelse:

1. **Arkitekturdiagram** (Mermaid C4 – Context + Container).
2. **Mappestruktur** for monorepoet med forklaring per katalog.
3. **Kjernekontrakter** (interfaces/abstrakte klasser) – fullstendig kode.
4. **Referanseimplementasjon av `UptimeAnalyzer`-modulen** med enhetstester.
5. **Eksempel REST-datakildeadapter** med Polly/tenacity, Key Vault-integrasjon og Managed Identity.
6. **Bicep/Terraform** for minimums-Azure-miljø.
7. **CI/CD-pipeline** (GitHub Actions) med OIDC, tester, container-build, Trivy-scan og deploy.
8. **README** med lokal oppstart, hvordan legge til ny modul, og security-notater.

---

## Teknologivalg (svar på disse før du begynner å kode)

Før du genererer kode, bekreft eller foreslå alternativer til:

- **Språk/runtime:** .NET 8 (C#) eller Python 3.12 – hvilket passer best gitt målet om Azure-native utvikling og fremtidig ML-integrasjon?
- **API-rammeverk:** ASP.NET Core Minimal API vs FastAPI.
- **Datalagring:** Time-series i Azure Data Explorer (ADX/Kusto) vs TimescaleDB på Azure. Eventer i Cosmos DB vs Postgres.
- **Message bus:** Event Hub (high-throughput) vs Service Bus (ordered, transactional).
- **Orchestration:** Container Apps vs AKS vs Functions – hvilket nivå av kompleksitet er berettiget?

Begrunn valgene med tanke på kost, drift og fremtidig skalering.

---

## Akseptansekriterier

- Ny modul skal kunne legges til uten å endre kjernen eller eksisterende moduler.
- Ingen hemmeligheter i kode, config-filer eller loggene.
- All Azure-tilgang via Managed Identity.
- Enhetstester med minst 80 % dekning på kjernen og UptimeAnalyzer.
- Kjører lokalt med `docker compose up` og i Azure med ett `azd up`-kall.
- README forklarer hvordan en utvikler legger til en ny modul på under 30 minutter.

---

## Fallgruver jeg vil du flagger eksplisitt

- Dataspill mellom tidssoner (UTC vs lokaltid) – kraftdata er beryktet for dette.
- Hull i tidsserier vs. faktisk nedetid – hvordan skiller vi "ingen data" fra "nedetid"?
- IEC 61400-26 kategorier for vindkraft (Information Availability vs Technical Availability) – de betyr forskjellige ting.
- Key Vault-throttling ved høy-frekvent secret-lookup – bruk cache.
- Managed Identity fungerer ikke lokalt uten `DefaultAzureCredential` med Azure CLI-fallback.
- Event Hub partitioning-valg er vanskelig å endre senere.

---

## Start her

Begynn med å stille oppklarende spørsmål om:
1. Hvilken type kraftverk (vann/vind/sol/termisk) som er primær use case – det påvirker dataskjema.
2. Forventet datavolum (antall målepunkter × frekvens) – avgjør lagringsvalg.
3. Om det finnes eksisterende SCADA/historian som skal integreres.
4. Compliance-krav utover GDPR (NIS2, NVE-forskrifter, IEC 62443 for OT-sikkerhet).

Deretter presenterer du valgt teknologi-stack med begrunnelse, og venter på bekreftelse før du genererer kode.

---

## Del 1 – Faktisk datakilde (oppgjørsdata fra Nord Pool / Elhub / eSett)

Første datasett som skal analyseres er månedlig oppgjørseksport for småkraftverket **Drivdal** (vannkraft, ~2,2 MW). Filstruktur:

**Fane `Summering`** – aggregert for perioden (f.eks. `01.02.2025 – 28.02.2025`).
Kolonner: `Tidsserie, MWh-Elhub, MWh-eSett, Spotbud, Spotomsetning, Ubalanse, RK-kjop, RK-salg, Nord Pool gebyr, eSett volumgebyr, eSett ubalansegebyr, Sum salg, Meglerprovisjon, Oppgjor`.

**Fane `<verk>`** (f.eks. `1 Drivdal`) – timeoppløsning, 1 rad per time for hele perioden.
Rad 0 = kolonnenavn, rad 1 = enheter (MWh / NOK / NOK/MWh), rad 2+ = data.
Kolonner: `Time, MWh-Elhub, MWh-eSett, Spotbud, Spotpris, Spotomsetning, Ubalanse, RK-pris, RK-kjop, RK-salg, Nord Pool gebyr, eSett volumgebyr, eSett ubalansegebyr, Sum salg, Meglerprovisjon, Oppgjor, <tom>, Brutto omsetning, Produksjonplan, Effektavlesninger, Absolutt ubalansevolum, Tap/gevinst ubalanse eks. gebyr`.

### Implementer i modulen `SettlementDataSource`
- Leser Excel-eksporten (pandas/openpyxl). Parseren må tåle at rad 1 er enhetsrad og at kolonne 17 er tom spacer.
- `Time` parses med `dayfirst=True` og lagres som UTC (kilden er Europe/Oslo – håndter sommertid eksplisitt, spesielt 24→25 timer i oktober og 24→23 timer i mars).
- Valider at `MWh-Elhub == MWh-eSett` (i dagens data stemmer det – bruk avvik som datakvalitetsflagg).
- Valider at Summering-fanen matcher sum av timerader (sanity check).

### Oppetidsmodul – hva settlement-data **kan** si oss (Del 1)
Modulen `UptimeAnalyzer.Settlement` utleder *proxy*-tilgjengelighet ut fra kun oppgjørsdata:

| Indikator | Beregning |
|---|---|
| **Produksjonstimer** | Antall timer med `MWh-Elhub > 0` |
| **Null-produksjonstimer** | Antall timer med `MWh-Elhub == 0` |
| **Proxy-tilgjengelighet** | `Produksjonstimer / Totaltimer` (f.eks. 391/672 = 58,2 %) |
| **Effektutnyttelse** | `SUM(MWh-Elhub) / (Pnominell × Totaltimer)` (kapasitetsfaktor) |
| **Planavvik** | `MWh-Elhub − Produksjonplan` per time – flagg systematiske avvik |
| **Ubalansekost** | `SUM(Absolutt ubalansevolum × RK-pris)` – indirekte kostnad ved ustabil drift |
| **Nedetidsklynger** | Sammenhengende 0-produksjonsperioder ≥ N timer – kandidater for nedetidshendelser |

### Viktig begrensning (flagg tydelig i rapport)
Settlement-data **kan ikke alene** skille mellom:
- Planlagt utkobling (revisjon, vedlikehold)
- Tvungen utkobling (feil, trip)
- Markedsstyrt stopp (spotpris < marginalkostnad)
- Tørrvær / lav vassføring (hydrologisk begrensning, ikke nedetid)
- Manglende måledata (Elhub-sending mangler)

Derfor er klassifiseringen i Del 1 **proxy**. Reell teknisk tilgjengelighet krever SCADA (Del 2).

---

## Del 2 – SCADA-integrasjon (planlagt modul)

På sikt skal SCADA-data implementeres i oppetidsanalysen. Modulen designes *nå*, selv om den ikke implementeres før senere, slik at kontraktene i kjernen er riktige og settlement-analysen senere kan berikes uten refaktorering.

### Arkitekturprinsipp
- Introduser et **Asset Event-skjema** i kjernen (ikke i modulen) som både `SettlementDataSource` og `ScadaDataSource` produserer.
- Alle moduler konsumerer `AssetEvent` – ingen leser Excel eller OPC UA direkte.

```text
AssetEvent {
  assetId, timestampUtc, source (settlement|scada|manual),
  measurement (Power_MW, State, Alarm, Availability_Flag, WaterLevel, ...),
  value, quality (good|uncertain|bad), unit, tags[]
}
```

### SCADA-adaptere – forhåndsdefiner, implementer senere
Lag en `IScadaDataSource`-kontrakt som støtter flere protokoller. Implementer stub + tydelig TODO for hver:

1. **OPC UA** (`asyncua` / `Opc.Ua.Client`) – standard for moderne kraftverk. Sertifikat-basert auth, subscribe på noder.
2. **IEC 60870-5-104** – utbredt i eldre vann- og nettstasjoner.
3. **Modbus TCP** – for PLS-nær data.
4. **Historian-eksport** – CSV/Parquet-dump fra OSIsoft PI, AVEVA, Cognite Data Fusion, eller lokal SQL. Ofte eneste praktiske vei i produksjon.
5. **MQTT/Sparkplug B** – for IIoT-gateways (f.eks. via Azure IoT Hub).

### OT-sikkerhet (kritisk – SCADA berører OT-nettet)
- **Aldri** direkte nett-tilkobling fra sky til SCADA. Bruk enveis datadiode eller IoT Edge / Azure Arc-gateway i DMZ.
- Følg **IEC 62443-3-3** (systemkrav) og **NVE-veilederen for IKT-sikkerhet i kraftforsyningen**.
- Segmentér iht. **Purdue-modellen**: SCADA-data hentes fra Level 3 (Historian), aldri direkte fra Level 2 (control).
- Signér og krypter all data på gateway før den forlater anleggsnettet.
- Loggfør alt som leses – immutable audit-logg kreves av NIS2 og NVE.
- Vurder **Azure Private Link + Private Endpoints** for all sky-tilkobling. Ingen public endpoints på SCADA-ruten.
- NIS2 stiller krav til hendelsesrapportering innen 24 t – bygg inn varslingsmekanisme.

### Berikelsen settlement × SCADA gir
Når begge kilder er koblet sammen kan `UptimeAnalyzer.Fused` skille:

| Hendelse | Settlement | SCADA | Tolkning |
|---|---|---|---|
| `MWh = 0`, State = `Running`, Alarm = none | ✓ | ✓ | Måledata mangler – rapporter til Elhub |
| `MWh = 0`, State = `Stopped (Planned)` | ✓ | ✓ | Planlagt utkobling – tell som Available |
| `MWh = 0`, State = `Tripped`, Alarm aktiv | ✓ | ✓ | **Tvungen utkobling** – reell nedetid |
| `MWh = 0`, State = `Running`, WaterLevel < min | ✓ | ✓ | Hydrologisk begrensning – ikke nedetid |
| `MWh < Plan`, State = `Derated` | ✓ | ✓ | Redusert kapasitet – partial outage |

Dette er mønsteret som gir faktisk teknisk tilgjengelighet iht. **IEEE 762** (termisk/hydro) og **IEC 61400-26** (vind).

### Leveranser når SCADA-modulen bygges
- `ScadaDataSource.OpcUa` (primær) + én historian-adapter.
- Tidsalignering mot settlement-data (interpolasjon/resampling til timeoppløsning).
- Konfliktløsning når de to kildene er uenige (settlement er autoritativ for MWh-tall mot Elhub, SCADA er autoritativ for driftstilstand).
- Rapport som viser begge perspektiver side om side.

---

## Oppdatert start-instruksjon

Når du genererer kode:
1. Bygg `SettlementDataSource` og `UptimeAnalyzer.Settlement` først – basert på Drivdal-filstrukturen over.
2. Definer `AssetEvent`-skjemaet og `IScadaDataSource` i kjernen samtidig, men lever kun stub-implementasjon av SCADA.
3. Design `UptimeAnalyzer.Fused` som interface/abstrakt, med tom implementasjon som kaster `NotImplementedException`/`NotImplementedError` inntil begge kilder er koblet.
4. Inkluder enhetstester basert på den faktiske Drivdal-eksporten (placer testfilen i `tests/fixtures/`).

---

## Anbefalte metoder for oppetidsanalyse av vannkraftverk

Denne seksjonen konsoliderer bransjestandarder og beste praksis som skal implementeres direkte i `UptimeAnalyzer`-modulene. Følg disse rammeverkene for å sikre at resultatene er sammenlignbare på tvers av verk og mot bransjebenchmarks.

### Primære standarder å følge

**IEEE 762 (2006/2023) – "IEEE Standard Definitions for Use in Reporting Electric Generating Unit Reliability, Availability, and Productivity"** er den autoritative referansen. Den skiller konsekvent mellom:
- *Reliability* – evnen til å levere (hendelsesfrekvens).
- *Availability* – andel tid enheten kan levere (tidsbasert).
- *Productivity* – faktisk levert energi mot maksimalt potensial (energibasert).

**NERC GADS (Generating Availability Data System)** gir den praktiske rapporteringsmekanikken og en moden hendelseskodeliste – bruk denne som utgangspunkt for event-skjemaet i `AssetEvent.state`.

**IEC 61400-26-serien** er formelt for vindkraft, men metodikken for å skille *Information Availability* fra *Technical Availability* og *System Availability* er direkte overførbar og nyttig i `Fused`-modulen.

**NVEs vannkraftdatabase og rapporteringsformat** brukes for nasjonal benchmarking; produksjonstall rapporteres allerede via Elhub, men statistikk på driftsavvik og revisjoner bør struktureres slik at de kan eksporteres i NVE-kompatibel form.

### Tilstandsklassifisering (Unit State Model)

Implementer følgende tilstandsmaskin per kraftverksenhet. Hver time skal tilordnes nøyaktig én hovedtilstand. Kodene følger IEEE 762 / NERC GADS:

| Kode | Tilstand | Teller mot | Hydro-relevans |
|---|---|---|---|
| **IS** | In Service | Service Hours (SH) | Normal produksjon |
| **RS** | Reserve Shutdown | Available Hours (AH) | Tilgjengelig, men ikke kjørt (markedsstyrt) |
| **PO** | Planned Outage | Unavailable Hours | Planlagt revisjon, varslet > 4 uker |
| **MO** | Maintenance Outage | Unavailable Hours | Vedlikehold < 4 uker varsling |
| **FO** | Forced Outage | Unavailable Hours | Trip, uvarslet stopp |
| **D1–D4** | Forced Derating | Partial Unavailable | Redusert kapasitet pga. feil |
| **PD / MD** | Planned / Maint. Derating | Partial Unavailable | Redusert kapasitet pga. vedlikehold |
| **IR** | Inactive Reserve | Ikke tilgjengelig | Mothball / mølig lagret |
| **RU** | Resource Unavailable | Se under – OMC | **Kritisk for hydro**: lav vassføring, miljøkrav |

### "Outside Management Control" (OMC) – kritisk skille for vannkraft

IEEE 762 introduserer eksplisitt *OMC*-kategorien: hendelser utenfor operatørens kontroll (vannmangel, miljøkrav, nettpålegg). Dette er **avgjørende for hydro**: en vanntørke skal ikke telle som nedetid i enhetsanalysen, men skal telle i systemanalysen.

Modulen skal derfor rapportere **to parallelle sett** av nøkkeltall:

1. **Unit Performance Indices** – ekskluderer OMC. Dette er "hvor god er driftsorganisasjonen?"
2. **System Reliability Indices** – inkluderer OMC. Dette er "hvor mye kan nettet stole på verket?"

### KPI-katalog som skal implementeres

Implementer alle følgende som rene funksjoner med enhetstester. Bruk IEEE 762-formler direkte – ikke egne avledninger.

**Tidsbaserte (timer):**
- `PeriodHours (PH)` = totalt antall timer i perioden
- `AvailableHours (AH)` = SH + RS
- `UnavailableHours (UH)` = PO + MO + FO
- `ServiceHours (SH)` = timer i tilstand IS
- `AvailabilityFactor (AF)` = AH / PH
- `EquivalentAvailabilityFactor (EAF)` = (AH − ekvivalente deratingtimer) / PH
- `ServiceFactor (SF)` = SH / PH
- `ForcedOutageRate (FOR)` = FOH / (FOH + SH)
- `EquivalentForcedOutageRate (EFOR)` = (FOH + EFDH) / (FOH + SH + EFDHRS)
- `EFORd` (demand-basert) – for enheter som ikke er kontinuerlig i drift; relevant for topplast / pumpekraft

**Energibaserte (MWh):**
- `CapacityFactor (CF)` = Faktisk produsert MWh / (P_nominell × PH)
- `OutputFactor (OF)` = Faktisk produsert MWh / (P_nominell × SH)
- `NetCapacityFactor` = bruk netto effekt (etter egenforbruk)

**Hendelsesbaserte:**
- `MTBF` (Mean Time Between Failures) = SH / antall FO
- `MTTR` (Mean Time To Repair) = FOH / antall FO
- `Availability_MTBF` = MTBF / (MTBF + MTTR)

**Hydro-spesifikke tilleggs-KPI-er (viktig):**
- `HydroResourceAvailability` = timer hvor vassføring ≥ minstekrav til produksjon
- `WaterUsageEfficiency` = MWh produsert / m³ vann brukt (kWh/m³)
- `SpillLoss` = vann forbipassert utenom turbin (kWh tapt økonomisk potensial)
- `EnvironmentalFlowCompliance` = timer med minstevannføring overholdt / PH
- `GrossHead / NetHead` – trykkhøyde; degradering over tid indikerer inntaksproblemer

### Rot­årsakskategorier (Cause Codes)

Bruk en standardisert taksonomi på to nivåer slik at mønsteranalyse er mulig. Forslag basert på NERC GADS tilpasset vannkraft:

1. **Turbin** – slitasje løpehjul, kavitasjon, lagerhavari, regulatorfeil
2. **Generator** – isolasjonssvikt, lagerfeil, kjøling
3. **Styresystem / PLS** – signalbortfall, programvarefeil, HMI
4. **Transformator / høyspenning** – vern utløst, vikling, bryter
5. **Vannvei** – inntaksrist (tilstopping), rørgate, luke, tetningslekkasje
6. **Nett** – frakobling fra netteier, frekvens/spenningsfeil
7. **Miljø / eksternt** – flom, ras, lynnedslag, fugl/dyr
8. **Ressurs (OMC)** – lav vassføring, minstevannføringskrav, islegging
9. **Marked (OMC)** – bevisst stopp ved lav spotpris (klassifiseres som RS, ikke FO)
10. **Mennesker / drift** – feiloperasjon, manglende bemanning, HMS-stopp

### Anbefalte analyseteknikker (implementer som egne under-moduler)

**1. Nedetidsklynging (sequence analysis)**
Grupper sammenhengende FO-hendelser med kort mellomrom (< konfigurerbar terskel, f.eks. 2 t) som én hendelse – ellers overtelles hendelsesraten. Bruk *run-length encoding* på tilstandsrekken.

**2. Pareto-analyse av årsakskoder**
80/20-fordeling av nedetid etter kategori og underårsak – standard rapport til produksjonssjef.

**3. Reliability Growth Analysis (Crow-AMSAA / Duane)**
Sporer om MTBF forbedres over tid etter tiltak. Standard metode innen RAM-analyse (Reliability, Availability, Maintainability).

**4. Weibull-analyse av feilintervaller**
Estimer formparameter β: β < 1 → barnesykdommer, β ≈ 1 → tilfeldige feil, β > 1 → slitasje. Gir input til vedlikeholdsstrategi.

**5. Power curve / performance deviation**
For hydro: plot MW mot netto trykkhøyde × vassføring. Avvik fra referansekurven indikerer slitasje (ofte løpehjul). Sett terskel for alarm ved vedvarende avvik > X %.

**6. Data Envelopment Analysis (DEA)**
Når flere aggregater finnes: rangér driftspunkter etter effektivitet. Refererte metoder i fagartiklene fra ScienceDirect og NREL – egen modul `UptimeAnalyzer.Benchmark`.

**7. Hendelsesdeteksjon i settlement-data (Del 1 proxy)**
Til SCADA er på plass: bruk regelbasert klassifisering for å skille sannsynlig forced/planned/resource:
- MWh=0 i hele sammenhengende døgn → sannsynlig planlagt (PO kandidat)
- MWh=0 i 1–6 t midt på døgnet + høy Spotpris → sannsynlig FO
- MWh=0 + Spotpris < estimert marginalkostnad → sannsynlig RS (markedsstyrt)
- MWh=0 i lengre periode + kjent lavflomsesong → sannsynlig RU
Flagg alle klassifiseringer med konfidensgrad og vent med endelig klassifisering til SCADA-data bekrefter.

### Datakvalitet og tidshåndtering (ofte undervurdert)

- **UTC internt, lokaltid ved presentasjon.** Norge er på Europe/Oslo med sommertid (CEST/CET). Håndter overgangsdøgnene – i slutten av mars har ett døgn 23 timer, i slutten av oktober 25 timer. Elhub rapporterer i lokaltid med eksplisitt tidssone – parse riktig.
- **Kvalitetsflagg per måleverdi** (good / uncertain / bad / substituted) – følger OPC UA-konvensjonen og bør persisteres gjennom hele pipelinen.
- **Manglende data ≠ nedetid.** Definér eksplisitt "Information Unavailable" som egen tilstand (IEC 61400-26-terminologi).
- **Avstemming mot Elhub.** Settlement-data er autoritativ for MWh-tall; avvik mot SCADA-integrert energi må forklares (ofte målepunkt-plassering: turbinklemme vs. nettovergivelse).

### Rapporteringsrytme

- **Daglig:** driftsstatusrapport, alarmer, avvik fra plan
- **Ukentlig:** tilgjengelighet (AF, EAF), produksjonsavvik, topp-5 nedetidshendelser
- **Månedlig:** full IEEE 762-KPI-rapport per verk + fleet-benchmark, årsaksfordeling (Pareto)
- **Kvartalsvis:** trendanalyse (Reliability Growth), vedlikeholdsprioritering
- **Årlig:** EAF, CF, lifetime-trend, kandidater for oppgradering/reinvestering

### Benchmark-verdier (indikative, for validering av beregninger)

- Nordiske vannkraftverk typisk **EAF 92–97 %**
- Kapasitetsfaktor: magasinkraft 35–55 %, elvekraft/small hydro **40–80 %**
- MTTR for småkraft typisk **4–48 t** avhengig av bemanningsmodell
- Hvis beregningene dine faller vesentlig utenfor disse intervallene – mistenk metodefeil før du rapporterer.

---

---

## Anleggskontekst: regulert vannkraft (magasin) med utvidelse til elvekraft

Første verk (Drivdal) er **magasinkraftverk** – produksjonen er operativt styrt mot marked og plan, ikke diktert av vassføring. Dette endrer hvordan null-produksjonstimer skal tolkes i klassifiseringsmotoren:

- Null-produksjon i et magasinverk er i utgangspunktet et **valg** (planlagt stopp, markedsstyrt RS, eller feil).
- Null-produksjon i et elvekraftverk er i utgangspunktet en **konsekvens** av hydrologi (ressursbegrensning RU), med mindre SCADA viser annet.

Prompten/modulen må derfor inneholde et `PlantType`-felt (`Regulated | RunOfRiver | Mixed | Pumped`) på verk-nivå, og klassifiseringsreglene i `UptimeAnalyzer.Settlement` må være *betingede på plant type*. Samme heuristikk på tvers av anleggstyper vil gi systematisk feil klassifisering.

I fremtidig utrulling på elvekraftverk skal modellen også kunne konsumere hydrologiske data (Nivå 1 i modenhetsstigen), men arkitekturen skal allerede nå være bygd slik at dette er en konfigurasjonsendring, ikke en refaktorering.

---

## Produksjonsplan-sammenligning (førstegrads-funksjonalitet, ikke bifunksjon)

Én av hovedgrunnene til at Excel-eksporten brukes fremfor direkte Elhub/eSett API-er er at portalen allerede inneholder `Produksjonplan` og `Spotbud` sammen med faktisk levert energi. Dette åpner for **3-veis avviksanalyse** som skal være en førstegrads-feature, ikke en bonus:

| Sammenligning | Viser | Typisk årsak til avvik |
|---|---|---|
| **Plan vs. Spotbud** | Hvor mye av planen ble faktisk budt inn i Day-Ahead | Bud-strategi, risikopåslag, manuell justering |
| **Spotbud vs. Elhub (faktisk)** | Leveranseavvik mot markedsforpliktelse | Feil, derating, regulerkraft-styring, måleavvik |
| **Plan vs. Elhub** | Totalt planavvik | Kombinasjon av over |
| **Plan – Spotbud – Elhub trio** | Full forsyningskjede-disposisjon | Rotårsaksanalyse av avvik |

### KPI-er å implementere
- `PlanFulfillment` = Σ Elhub / Σ Plan
- `BidAccuracy` = Σ Elhub / Σ Spotbud
- `PlanToBidDeviation` = (Plan − Spotbud) / Plan
- `ImbalanceCausedByDeviation` = korrelasjon mellom Absolutt ubalansevolum og (Spotbud − Elhub)
- `PlanDeviation_MWh` og `PlanDeviation_NOK` per time og aggregert

### Visualisering
Rapporten skal ha en time-for-time figur med alle tre serier (Plan, Spotbud, Elhub) + markering av avviksterskler, slik at driftssjef umiddelbart ser om avvik er systematiske eller enkelthendelser. Dette er hovedgrunnen til å bruke portaleksporten – mist ikke den funksjonaliteten i jakten på "ren" API-integrasjon.

---

## Strategi mot ombygging: hva bestemmes nå, hva utsettes

Brukeren har erfart at sen innføring av frontend-rammeverk (React) førte til omfattende ombygging av en tidligere applikasjon. Det skal ikke skje her. Svaret er ikke å bygge alt på én gang – svaret er å **låse grunnleggende valg tidlig** og **utsette skalerings-valg** til de faktisk trengs.

### Lås nå (valg som er dyre å endre senere)
Disse bestemmes i v1 og skal ikke reforhandles senere:
1. **Primærspråk/runtime** – anbefaling: .NET 8 + ASP.NET Core Minimal API.
2. **Frontend-rammeverk** – **må velges og besluttes i v1**, ikke legges til senere. Anbefaling for .NET-stack: **Blazor WebAssembly** (samme språk som backend, enklere deling av modeller, sterk TypeScript-erstatning) eller **React + Vite + TypeScript** (større økosystem, kan brukes om du senere vil splitte teamet). **Velg én – ikke senere.**
3. **Datamodellens kjerneobjekter** – `AssetEvent`, `ClassifiedPeriod`, `AssetInterval`, `DataQualityState` som førsteklasses typer. Legg inn `source`, `confidence`, `schemaVersion` fra dag én.
4. **Plugin-kontrakter** – `IDataSource`, `IAnalyzer`, `IReportSink`, `IAuthProvider`, `IScadaDataSource`, `IHydrologicalDataSource`, `ICmmsDataSource` defineres alle *nå*, selv om bare `ISettlementDataSource` (en spesialisering av `IDataSource`) implementeres.
5. **Multi-tenant / multi-plant skjema** – databasen skal fra dag én ha `plantId`, `plantType`, `ownerOrgId` som kolonner selv om første brukstilfelle er ett verk. Retrofit av multi-tenancy er en kjent dyr operasjon.
6. **Tidshåndtering** – UTC internt, eksplisitt tidssonekonvertering ved I/O. Aldri "naive" datetimes i domenemodellen.
7. **Autentisering/autorisasjon-sømmer** – identitetsleverandør (Entra ID) og interne auth-kontrakter låses nå, selv om selve brukertilgang-modulen bygges senere. Se egen seksjon under om hvordan dette forberedes.

### Utsett til faktisk behov (valg som er billige å endre senere)
Disse legges inn når de trengs – ikke før. Arkitekturen skal kun *forberede* for dem:
1. **Event Hubs** – in-proc domain events (MediatR eller tilsvarende) er nok i v1. Event Hub legges inn når en andre modul faktisk skal konsumere samme strøm.
2. **Azure Data Explorer (ADX)** – PostgreSQL håndterer timeoppløst data for noen titalls verk fint. ADX legges inn når datavolumet eller spørringstidene faktisk krever det.
3. **AKS** – Container Apps holder frem til dere har kompleksitet som virkelig krever Kubernetes.
4. **API Management + WAF** – enkel token-basert autentisering på Container Apps-ingress holder i v1. APIM legges inn når dere får eksterne konsumenter.
5. **Full CI/CD med OIDC og Bicep** – manual deploy med Azure Developer CLI (`azd`) er akseptabelt i v1, men *pipeline skal være skissert og dokumentert* slik at overgangen er en dags arbeid.

### Designprinsippet som forhindrer ombygging
**Alle "utsatt"-valg skal være *additive*, ikke *substitutive*.** Det betyr: det skal aldri være nødvendig å *fjerne* v1-kode for å legge til senere infrastruktur. Eksempler:

- Domain events via MediatR i v1 → i v2 legges Event Hub-publisering *i tillegg*, MediatR-koden består.
- PostgreSQL for tidsserie i v1 → i v2 legges ADX *i tillegg* for sanntids-spørringer, PostgreSQL beholder transaksjonell data.
- Ingen message bus i v1 → v2 introduserer bus som en ny `IMessagePublisher`-implementasjon som erstatter in-proc-versjonen via DI, uten endring i kallsteder.

Hvis et arkitekturvalg *ikke* tilfredsstiller det additive prinsippet, skal det låses nå.

### Modul-nivå utskiftbarhet (kjerneprinsipp)

Det er eksplisitt akseptabelt å bygge mange små, *naive* moduler i v1. Hver modul gjør én ting, enkelt, og oppgraderes uavhengig senere. For at dette skal fungere uten at oppgradering blir en ombygging, må følgende designregler følges strengt:

**1. Stabil offentlig kontrakt per modul.**
En modul eksponerer kun sitt interface (`IAnalyzer`, `IDataSource`, etc.) og sine domeneevents. Alt annet – interne klasser, lagring, algoritmer – er privat. Når en modul oppgraderes fra naiv til avansert, endres *kun* implementasjonen bak interface-grensen. Kallsteder er uendret.

**2. Ingen delt mutabel tilstand mellom moduler.**
Moduler kommuniserer via domain events eller via kjernens repository-abstraksjoner. En modul leser aldri en annen moduls interne tabeller, cache eller klasser. Dette er den vanligste kilden til "kan ikke oppgradere A uten å endre B" – elimineres ved prinsipp.

**3. Hver modul eier sin egen persistens (hvis den har noe).**
Hvis `UptimeAnalyzer.Settlement` har behov for mellomlager eller cache, ligger det i *dens eget* skjema/prefix (f.eks. `settlement.classifications`). Når modulen erstattes, erstattes også dens tabeller uten å røre andre moduler.

**4. Versjonerte events.**
Domain events har `schemaVersion`. Når en modul oppgraderes og endrer et event, publiseres v2 parallelt med v1 inntil alle konsumenter har migrert. Ingen "big bang"-brytninger.

**5. DI-basert registrering, ikke konkrete referanser.**
Oppgradering fra `SettlementAnalyzerV1` til `SettlementAnalyzerV2` skal være én linjes endring i DI-oppsettet. Hvis det krever endring av andre moduler som "vet" om den konkrete implementasjonen, har designet mislyktes.

**6. Feature flags for gradvis utrulling.**
En oppgradert modul kan kjøre parallelt med sin forgjenger i en overgangsperiode, styrt av feature flag per verk/kunde. Dette gir trygg utrulling uten å rulle tilbake v1-modulen helt.

**7. Hver modul er selvstendig testbar.**
Modulens tester bruker kun kjernens kontrakter og mocks – aldri andre moduler. Da er en oppgradering bevist isolert før den rulles ut.

### Hva denne strategien betyr for utvikling

Dette er eksplisitt tillatt og anbefalt i v1:
- `UptimeAnalyzer.Settlement` kan være en enkel 200-linjers klasse med regelbasert klassifisering.
- `SettlementDataSource` kan være en rett-frem pandas/openpyxl-parser uten caching eller parallellisering.
- `ReportGenerator` kan skrive til Excel med openpyxl uten fancy templating.
- Data-kvalitetssjekker kan være en flat liste av regler, ikke et fullt rammeverk.

Senere, når én av disse skal bli avansert (f.eks. `UptimeAnalyzer.Settlement` skal få ML-basert klassifisering), skal det være mulig å levere `SettlementAnalyzerV2` ved siden av V1, rulle det ut per verk via feature flag, og fjerne V1 når V2 er validert. Ingen andre moduler, ingen kallsteder, ingen DTO-er skal endres som følge av oppgraderingen.

**Testen er enkel:** hvis oppgradering av én modul krever Pull Request som berører flere modulmapper, har arkitekturen feilet prinsippet. Da skal det flagges og refaktoreres før oppgraderingen fortsetter.

---

## Håndtering av manglende og ufullstendige data (førsteklasses krav)

Manglende datasett i innkommende filer skal aldri føre til at hele importen feiler, og skal aldri stille omgjøres til antatte verdier uten eksplisitt regel. Følgende rammeverk skal implementeres i `SettlementDataSource` og gjenbrukes av alle fremtidige `IDataSource`-implementasjoner.

### Nivåer av "manglende data"

1. **Strukturell mangel** – kolonne, ark eller fil mangler helt.
2. **Radnivå-mangel** – forventet time/dag finnes ikke (hull i tidsserien).
3. **Verdinivå-mangel** – cellen er tom, "-", "N/A", eller NaN.
4. **Kryss-referanse-mangel** – `Produksjonplan` finnes men ikke `Elhub` (eller omvendt).
5. **Skjemaversjon-drift** – portalen har lagt til/fjernet/omdøpt kolonner siden sist.

### Felles policy (må gjelde alle datakilder)

**Aldri stille imputasjon.** Hvis en verdi er ukjent, representeres den som `null` i domenemodellen *med eksplisitt `DataQualityState`-markering*, ikke som 0 eller "antatt forrige verdi".

**Eksplisitt kolonnekontrakt per kilde.** Hver datakilde har et deklarert skjema:
```
required   – må finnes, ellers avvis raden/filen
expected   – bør finnes, men import kan fortsette uten
optional   – kan mangle, ingen konsekvens
derived    – beregnes ut fra andre kolonner hvis fraværende
```

**Tredelt håndteringsstrategi per hendelse:**
- **Reject** – raden/filen forkastes og sendes til dead-letter med årsak (gjelder brudd på `required`).
- **Quarantine** – importeres med `DataQualityState = Quarantined` og varsel til drift (gjelder brudd på `expected`).
- **Accept with flag** – importeres med `DataQualityState = Uncertain` (gjelder tomme `optional`).

**Persistér rå-innhold.** Originalfilen/API-responsen lagres umodifisert i Blob Storage med hash og tidsstempel, slik at enhver importfeil kan rekonstrueres uten å miste kilden. Dette er også et revisjonskrav.

### Konkret håndtering per manglende datasett

| Scenario | Strategi |
|---|---|
| Hele `Summering`-fane mangler | Accept – timefane alene er tilstrekkelig, men flagg for kryssvalidering senere |
| `Produksjonplan`-kolonne mangler | Accept med flagg, deaktivér Plan-KPI-er, ikke hele rapporten |
| `MWh-Elhub` mangler på enkelttimer | `DataQualityState = InformationUnavailable` – **ikke** klassifiser som nedetid |
| `Ubalanse` mangler | Beregn fra `MWh-Elhub − Spotbud` hvis begge finnes, ellers flagg |
| Hele timer mangler i sekvensen | Sett inn `InformationUnavailable`-rad med `null`-verdier for å bevare tidsseriens integritet |
| `Time`-kolonnen har huller over DST-overgang | Håndter eksplisitt: mars = 23 timer i overgangsdøgn, oktober = 25 timer |
| Negative `MWh-Elhub` | Flagg – kan være måleavvik, regulerkraft-kjøp, eller kolonnefortolkningsfeil |
| Kolonnenavn endret i nyere eksport | `SchemaRegistry` med versjonsdeteksjon + mapping til kanonisk navn |
| Fil er kryptert eller korrupt | Reject med tydelig feilmelding, original bevart i quarantine-blob |
| Verdi utenfor fysisk rimelig intervall (f.eks. MWh > 2× nominell) | Quarantine med begrunnelse |

### `DataQualityState` som førsteklasses type

Ikke kun som enum-flagg på `AssetEvent`, men som egen tilstand i `ClassifiedPeriod`-modellen. Verdier:

```
Good                – validert, fra autoritativ kilde
Uncertain           – akseptert, men med avvik som bør overvåkes
Substituted         – beregnet fra andre kolonner, ikke målt
InformationUnavailable – manglende data (≠ nedetid)
Quarantined         – importert men ekskludert fra KPI-beregninger inntil gjennomgått
Rejected            – avvist, bare i audit-log
```

KPI-beregningene skal ha eksplisitt regel for hver tilstand – typisk: `Good` og `Substituted` inkluderes, `Uncertain` inkluderes med lavere confidence-score, `InformationUnavailable` ekskluderes fra tellere og nevnere (teller som egen kategori), `Quarantined`/`Rejected` ekskluderes helt men rapporteres som datakvalitetsproblem.

### Datakvalitetsrapport (egen leveranse per import)

Hver importkjøring skal produsere en egen rapport:
- Total timer forventet / motatt / aksepterte / flagget / avvist
- Per kolonne: antall `null`, antall out-of-range, antall substituert
- Liste over avvik med alvorlighetsgrad
- Delta mot forrige import (har kolonner endret seg, har nye avvik dukket opp)

Dette er et *dashboard-borger*, ikke en log-fil. Driftspersonell skal kunne se datakvalitet før de ser KPI-er – for KPI på dårlige data er verre enn ingen KPI.

### Konsekvens for KPI-rapportering

Alle KPI-er rapporteres med tre tall:
- Verdien (f.eks. `AF = 0,94`)
- Antall timer den er basert på (f.eks. `basert på 672/672 timer`)
- Datakvalitetsscore (f.eks. `quality: Good (98 %)`)

Dette er ikke-forhandlingsbart. Rapporter uten kvalitetskontekst skal ikke godkjennes av systemet.

---

## Retrofit-fellene: sømmer som må være på plass i v1

Følgende områder er identifisert som klassiske "kan-ikke-legges-til-senere-uten-ombygging"-tilfeller. Alle skal ha sin abstraksjon i kjernen fra v1, med enkel default-implementasjon. Senere utvidelse blir da en ny implementasjon, ikke en refaktorering.

### 1. Async og bakgrunnsjobber – `IJobQueue`

**Hvorfor:** Settlement-import av historiske måneder tar minutter. Rapportgenerering, SCADA-polling og klassifiserings­pipelines skal ikke blokkere HTTP-kall. Retrofit fra synkron til async krever uthenting av arbeid fra endepunkter – ofte en full omarbeidelse av kontroll­flyten.

**Sømmen:** `IJobQueue`-interface i kjernen med `Enqueue<TJob>()` og `HandleAsync()`. v1 har in-process implementasjon basert på .NET Channels. Senere legges Azure Service Bus eller Storage Queues til som ny implementasjon via DI.

**Regel:** Alle operasjoner som *kan* ta mer enn 2 sekunder, skal gå gjennom `IJobQueue` også i v1 – selv om køen er in-memory. Dette tvinger frem riktig flyt fra første dag.

### 2. Databasemigreringer – fra dag én

**Hvorfor:** Ad-hoc SQL-endringer fører til at ingen miljø er i samme tilstand. Retrofit til migreringsverktøy krever ofte å rekonstruere historikken manuelt.

**Sømmen:** EF Core Migrations (for .NET) eller DbUp/Flyway. Hver skjema­endring er en versjonert migrering. Applikasjonen kjører migreringer ved oppstart i dev, og som egen steg i CI/CD for prod.

**Regel:** Aldri `ALTER TABLE` direkte mot en database. Aldri "manuell fix" av et miljø.

### 3. API-versjonering – fra første endepunkt

**Hvorfor:** Å legge til `/v1`-prefikset senere bryter alle eksisterende klienter. Dalane-Kraft, fremtidige portaler og integrasjonspartnere blir raskt klienter.

**Sømmen:** Alle endepunkter under `/api/v1/...`. Bruk `Asp.Versioning.Http` (ASP.NET Core versioning) med URL- eller header-basert versjonering. Fremtidige breaking changes introduseres som `/v2` parallelt.

**Regel:** Ingen endepunkter uten versjons-prefiks.

### 4. Paginering i alle list-endepunkter

**Hvorfor:** "Gi meg alle events" fungerer for Drivdal februar 2025 (672 timer). Det fungerer ikke for 10 verk i 5 år. Retrofit av paginering bryter hver klient.

**Sømmen:** Standard response-envelope med `items[]`, `nextCursor`, `pageSize`. Maks side-størrelse håndhevet i middleware. Cursor-basert anbefales over offset for tidsserie­data.

**Regel:** Ethvert endepunkt som kan returnere mer enn N (f.eks. 100) elementer, er paginert fra dag én.

### 5. Storage-abstraksjon – `IFileStorage`

**Hvorfor:** Settlement-Excel opplastet via portal, genererte PDF-rapporter, rå-kopier av API-responser – alle skal til et sted. Hvis v1 skriver til `/var/app/uploads`, er migrering til Blob Storage en sårbar operasjon.

**Sømmen:** `IFileStorage`-interface med `Put`, `Get`, `Delete`, `List`. v1 i dev: lokal filsystem. v1 i Azure: Blob Storage. Samme interface.

**Regel:** `System.IO.File` finnes aldri i modulkode. Kun bak `IFileStorage`.

### 6. Caching-abstraksjon – `IDistributedCache`

**Hvorfor:** Key Vault-throttling (allerede flagget), fremtidig caching av KPI-beregninger og rapport-artefakter. Retrofit av cache krever endring på hvert bruksted.

**Sømmen:** Bruk Microsofts `IDistributedCache` direkte. I v1: `AddDistributedMemoryCache()`. Senere: Azure Redis Cache via samme interface.

**Regel:** Egenbygd caching forbudt. Alltid gjennom `IDistributedCache`.

### 7. Per-verk konfigurasjon som data

**Hvorfor:** Terskler, KPI-mål, klassifiseringsregler, rapporteringsintervall, nominell effekt, minimum operasjonell vassføring – alt varierer per verk. Hardkoding av Drivdal-verdier gjør at hvert nytt verk krever utvikler og ny deploy.

**Sømmen:** Tabell `plant_configuration` med `plantId`, `key`, `value`, `valueType`, `effectiveFrom`. Tjeneste `IPlantConfiguration.Get<T>(plantId, key)` med caching. Admin-UI kommer senere, men databaseskjemaet og lesetjenesten eksisterer fra dag én.

**Regel:** Ingen kraftverk-spesifikke tall i kode. Alle går gjennom `IPlantConfiguration`.

### 8. Notifikasjonstjeneste – `INotificationService`

**Hvorfor:** Import feilet, terskel brutt, rapport klar, sertifikat utløper. Alle er hendelser som krever varsel. Retrofit av varsling krever å finne hver hendelses-sender og instrumentere manuelt.

**Sømmen:** `INotificationService.Send(topic, payload, recipients)` i kjernen. v1 skriver til structured log. v2 legger til Azure Communication Services (epost), Teams adaptive cards, webhook – alle som implementasjoner av `INotifier`, rutet etter `topic`.

**Regel:** Alle hendelser som *kan* bli varsler i fremtiden, går gjennom `INotificationService` allerede nå – selv om mottaker er "log only".

### 9. Soft delete og data-retensjon

**Hvorfor:** GDPR, NVE-revisjon, og brukerens forventede "undelete"-mulighet. Retrofit av soft delete krever skjema­migrering og gjennomgang av hver delete-spørring.

**Sømmen:** Alle domeneentiteter har `DeletedAt` (nullable timestamp) og `DeletedBy`. Global query filter i EF Core som ekskluderer slettede. Retensjonspolicy i konfig per datakategori (eksempel: settlement-data 10 år, importkvitteringer 90 dager).

**Regel:** `DELETE FROM` finnes ikke i modulkode. Alt er "mark as deleted" gjennom repository.

### 10. Eksportformat-matrise – `IReportRenderer`

**Hvorfor:** Første rapport er Excel. Neste blir PDF (for leverandør/revisor), deretter CSV (for egne analytikere), deretter JSON (for webhook-integrasjon).

**Sømmen:** `IReportBuilder` produserer en abstrakt `ReportModel`. `IReportRenderer` pr. format konverterer til konkret utdata. Legge til ny format = ny renderer, ingen endring i rapportlogikken.

**Regel:** Formatering og domenelogikk er alltid skilt. Rapportkoden bygger abstrakt, renderne konkretiserer.

### 11. Konfigurasjonsvalidering ved oppstart

**Hvorfor:** Feil i appsettings.json som først oppdages midt i produksjonskjøring er kostbar. Retrofit krever gjennomgang av alle konfig-bruksteder.

**Sømmen:** Alle konfig-seksjoner som sterkt typede `IOptions<T>` med `[Required]`, `[Range]` etc. Validering kjøres ved oppstart (`ValidateOnStart()`), feil konfig → feil ved start, ikke ved bruk.

**Regel:** `Configuration["key"]` forbudt i modulkode. Alltid `IOptions<T>`.

### 12. Tenant-bevisst observabilitet

**Hvorfor:** Når Dalane-Kraft blir flere organisasjoner, må logger kunne filtreres per `ownerOrgId`. Retrofit krever å finne hvert logg-punkt og legge til tag.

**Sømmen:** Middleware som setter `ownerOrgId`, `plantId`, `userId`, `correlationId` som log-scope/baggage via OpenTelemetry. Alle logger arver disse automatisk.

**Regel:** Ingen logg skal sendes uten kontekst. Dette skjer via middleware, ikke manuelt i modul.

### 13. Domain events som førsteklasses konstrukter

**Hvorfor:** "Import fullført", "klassifisering endret", "terskel brutt" – alt dette vil andre moduler lytte på senere. Uten event-modell må konsumenter kobles direkte til produsenter.

**Sømmen:** MediatR `INotification` for in-proc events i v1. Alle forretningshendelser publiseres som events med `eventId`, `occurredAt`, `correlationId`, `schemaVersion`. Senere legges `IEventPublisher` til for ekstern fan-out (Event Hub / webhooks) – eksisterende publisering endres ikke.

**Regel:** Forretningshendelser publiseres aldri som direkte metodekall mellom moduler. Alltid som events.

### 14. Webhook-abonnements­modell forberedt

**Hvorfor:** Dalane-Kraft vil etter hvert ville eksponere "rapport klar"-eller "nedetidsvarsel" til Statnett, leverandører, eller intern CMMS. Retrofit krever å identifisere hvilke events som kan abonneres på.

**Sømmen:** Tabell `event_subscription` med `eventType`, `callbackUrl`, `secret`, `enabled`. Tjeneste `IEventPublisher` som ved publisering matcher abonnementer og legger utgående HTTP-kall i `IJobQueue` (retry + DLQ). v1 har ingen abonnenter – modellen ligger klar.

**Regel:** Ingen modul kaller webhook direkte. Alt går via `IEventPublisher`.

### Dokumenterte som planlagte utvidelser (ikke låst nå)

Følgende kan legges til senere uten vesentlig ombygging, forutsatt at kodeorganiseringen holder presentasjon/strenger i et lag:

- **Internasjonalisering:** ressursfiler (`.resx` / i18n JSON) kan introduseres ved behov. Anbefales likevel å holde alle brukertekster i komponenter/tjenester (ikke i domene­kjernen) slik at ekstraksjon blir triviell.
- **GDPR data subject rights:** ende-til-ende data-eksport og sletting kan bygges som egen modul som leser på tvers av andre moduler.
- **Tidsserie-sharding:** aktuelt ved 10+ verk og minutt-oppløsning. Postgres tåler mye uten.

### Validering av retrofit-prinsippet

For hvert av punktene over: hvis en fremtidig oppgradering (f.eks. introduksjon av Redis, Azure Service Bus, webhook-konsumenter) krever endring i flere modulmapper, har sømmen vært utilstrekkelig. Da skal den strammes før oppgraderingen fortsetter.

---

## Brukertilgang og autorisasjon: sømmer nå, modul senere

Appen skal til slutt brukes av Dalane-Kraft med flere brukere i ulike roller. Selve brukertilgang-modulen bygges ikke i v1, men **sømmene for den må være på plass fra dag én** slik at senere introduksjon blir en additiv leveranse som ikke rører andre moduler.

### Forskjell på autentisering og autorisasjon

**Autentisering** (hvem er brukeren) er enkel å legge til senere hvis kontraktene er riktige.

**Autorisasjon** (hva får brukeren se og gjøre) er vanskelig å legge til senere hvis forretningslogikken ikke er skrevet med brukerkontekst i bakhodet. Dette er den typiske fellen. Derfor må filter­punktene finnes *nå*, selv om de er no-op i v1.

### Sømmer som må bygges inn i v1

**1. `ICurrentUser` / `IUserContext` som kjernetype.**
Interface i kjernen med brukerens ID, rolle, tilhørende organisasjon og tilgangsomfang. I v1 én implementasjon: `SystemUserContext` som alltid returnerer "admin" med full tilgang. Alle tjenester som trenger "hvem spør?" injiseres `ICurrentUser`. Når auth-modulen kommer byttes implementasjonen i DI-oppsettet – andre moduler endres ikke.

```csharp
// src/KraftverkUptime.Core/Security/ICurrentUser.cs
public interface ICurrentUser
{
    string UserId { get; }
    string OrgId { get; }
    IReadOnlySet<string> Roles { get; }
    IReadOnlySet<string> AccessiblePlantIds { get; }  // null = all
}
```

**2. `IQueryContext` på alle repository-kall.**
Alle spørringer går gjennom en kontekst som beriker filter før SQL genereres. I v1 returnerer kontrakten "ingen filter". Når auth-modulen kommer, beriker den automatisk filteret med `ownerOrgId` og `plantId`-tilganger – eksisterende spørringer endres ikke.

**3. Policy-basert autorisasjon på alle API-endepunkter fra dag én.**
Alle endepunkter merkes med policyer, selv om policy-implementasjonen i v1 bare returnerer "allow". Bruk faste policynavn som senere får ekte innhold:

```csharp
[Authorize(Policy = Policies.PlantReader)]
app.MapGet("/api/plants/{plantId}/uptime", ...);
```

Policy-navn som skal låses nå: `PlantReader`, `PlantAnalyst`, `PlantAdmin`, `OrgAdmin`, `SystemAdmin`.

**4. Audit-logging som førsteklasses tjeneste.**
Alle skrivende operasjoner logger "hvem gjorde hva" gjennom en `IAuditLogger`-tjeneste. I v1 skrives "system" som aktør. Når auth-modulen kommer fylles feltet automatisk med faktisk bruker. Retrofit av audit i hver skrivende modul er kostbart – derfor bygges det inn nå.

**5. Entra ID som låst identitetsleverandør.**
Dalane-Kraft bruker etter all sannsynlighet Microsoft 365/Entra ID. Låsing nå gjør at OIDC-flyt, token-validering og app-registreringer er konsistent gjennom frontend, backend og API-gateway. Retrofit til annen IdP krever samtidig endring på tre lag – unngås ved å velge nå.

**6. Frontend har `UserContextProvider` fra dag én.**
Selv om Blazor/React i v1 viser "Systembruker", finnes en provider som komponenter konsumerer. Når ekte login introduseres byttes provider ut, ikke komponenter.

**7. Alle domeneentiteter har `ownerOrgId` og `plantId` fra dag én.**
Allerede spesifisert under "Lås nå", men viktig å gjenta: uten disse kolonnene er radnivå-autorisasjon umulig uten databasemigrering og backfill.

### Hva auth-modulen faktisk blir når den legges til (v2-leveranse)

Modul: `KraftverkUptime.Auth`. Leverer ved registrering:
- Erstatning for `ICurrentUser` som leser Entra ID-token og henter brukerens roller og tilgangsomfang.
- Policy-implementasjoner bak de låste policynavnene (`PlantReader` etc.).
- Brukeradministrasjonsflate (invitasjon, rolletildeling per verk).
- Berikelse av `IQueryContext` med `ownerOrgId` og plant-filter.
- Automatisk utfylling av faktisk bruker-ID i audit-logger.
- Frontend-routing for login/logout og rolle-betinget UI-visibilitet.

### Foreslått rollemodell (forbered nå, aktivér senere)

| Rolle | Tilgang |
|---|---|
| `SystemAdmin` | Full tilgang, inkludert kryss-organisasjon (kun Anthropic-team/drift) |
| `OrgAdmin` | Administrerer brukere og verk innenfor egen organisasjon (Dalane-Kraft) |
| `PlantAdmin` | Konfigurerer ett eller flere spesifikke verk (datakilder, terskler, plan) |
| `PlantAnalyst` | Leser alle data + kjører analyser + eksporterer rapporter |
| `PlantReader` | Leser rapporter og dashbord, ingen eksport eller konfigurering |

Policynavnene brukes allerede på endepunktene i v1. Når modulen kommer får de innhold.

### Validering av prinsippet

Testen er konkret: PR-en som introduserer `KraftverkUptime.Auth` skal bare inneholde filer i den nye modulmappen pluss *én linjes endring i composition root* (`Program.cs`) der den gamle `SystemUserContext` byttes med `EntraIdUserContext`. Hvis noen annen modul må endres, har en av sømmene over vært utilstrekkelig og skal rettes før auth rulles ut.

### Konsekvens for v1-leveransen

Dette betyr at AI-modellen som bygger v1 må levere:
- `ICurrentUser`, `IQueryContext`, `IAuditLogger` som interfaces i kjernen.
- `SystemUserContext` som v1-implementasjon.
- Policy-registrering med no-op handlers bak alle fem rollenavn.
- `ownerOrgId`/`plantId`-kolonner og filtreringsabstraksjonen i repository-laget.
- Test som verifiserer at bytte til en mock `ICurrentUser` endrer spørringsfilter uten å røre modulkoden.

---

## Leveranseformat – minimér arbeid for bruker

Alle svar fra deg skal leveres slik at brukeren har **minst mulig manuelt arbeid** for å gå fra svar til kjørende/testet resultat. Dette er et førsteklasses krav, ikke et "nice to have". Følg disse reglene konsekvent:

### Kodeleveranser

- **Komplette filer, ikke snutter.** Lever hver fil i sin helhet med alle imports, namespace, using-direktiver. Aldri `// ... resten av koden ...` eller `# existing code unchanged`.
- **Eksakt filsti for hver fil.** Angi full relativ sti fra prosjektroten øverst i hver kodeblokk: f.eks. `// src/KraftverkUptime.Core/Contracts/IDataSource.cs`.
- **Én kodeblokk per fil.** Ikke del opp samme fil i flere blokker med kommentarer imellom.
- **Fullstendig `using`/`import`-liste.** Brukeren skal aldri måtte legge til imports manuelt.
- **Ingen TODO-er uten plan.** Hvis noe er utsatt, skriv `// TODO(v2): beskrivelse – se seksjon X i prompten`, ikke bare `// TODO`.

### Prosjektoppsett

- **Lever én kommando for initiell scaffold.** Eksempel: `dotnet new sln && dotnet new classlib -o src/KraftverkUptime.Core && ...` – komplett kommandorekke, ikke fragmenter.
- **Inkluder `.csproj` / `pyproject.toml` / `package.json`-filer i leveransen** med alle avhengigheter spesifisert og versjoner pinned.
- **Lever `docker-compose.yml`** som starter alt brukeren trenger lokalt med én kommando: `docker compose up`. Inkluder PostgreSQL, eventuelle stubs/mocks, og selve appen.
- **`.env.example`** med alle nødvendige miljøvariabler og eksempelverdier som fungerer for lokal utvikling (ikke skarpe hemmeligheter).

### Testing

- **Testfiksturer inkludert.** Legg Drivdal-filen i `tests/fixtures/drivdal-feb2025.xlsx` og referer til den i tester. Brukeren skal aldri måtte laste ned eller kopiere data for å kjøre tester.
- **Tester skal kjøre med én kommando.** `dotnet test` eller `pytest` skal fungere umiddelbart etter `docker compose up`.
- **Testnavn beskriver scenario.** `UptimeAnalyzer_WhenAllHoursHaveZeroProduction_ReturnsAvailabilityProxyWithLowConfidence` – ikke `Test1`.
- **Forventede utdata lagret som fixture** i `tests/expected/` slik at regresjon fanger utilsiktede endringer i KPI-verdier.
- **Én seeding-kommando** for lokal database: `dotnet run --project tools/Seed` eller `python -m tools.seed` som laster Drivdal-data inn i lokal Postgres.

### Azure-oppsett

- **`azd up` skal fungere end-to-end.** Bruk Azure Developer CLI-maler slik at én kommando provisjonerer alle ressurser og deployer appen.
- **Alle Bicep-parametere med dokumenterte defaults** og én `main.parameters.json` per miljø (dev/prod).
- **Oppgi nøyaktige Azure-ressursnavn** (eller navngivingsmønster), ikke `<your-resource-group-name>`.
- **Preflight-sjekkliste** øverst i hver deploy-guide: "Før du kjører dette, bekreft: (1) Azure-abonnement aktivt, (2) `az login` kjørt, (3) owner-rolle på subscription eller egen RG, (4) `azd` installert versjon ≥ X.Y."
- **Teardown-kommando tydelig angitt.** `azd down --force --purge` så eksperimenter er trygge og koster null når de ikke brukes.

### Dokumentasjon per leveranse

- **Én README.md per modul** med:
  - *Formål*: én setning.
  - *Avhengigheter*: hva denne modulen trenger fra andre moduler.
  - *Kjør lokalt*: eksakte kommandoer.
  - *Kjør tester*: eksakt kommando.
  - *Utvidelsespunkter*: hvilke interfaces/hooks som kan erstattes senere.
- **Arkitekturdiagram som Mermaid i README** – ikke ekstern lenke til bilde.
- **Eksempel-output inkludert.** Når du leverer en KPI-beregning, vis også hvordan rapporten ser ut for Drivdal-februar – gjerne som tabell i README.

### Responsformat når du svarer brukeren

- **Start med ett-avsnitts sammendrag** av hva som leveres og hva brukeren skal gjøre.
- **Så: nummerert liste med nøyaktige brukerhandlinger** i rekkefølge – "kopier disse filene", "kjør denne kommandoen", "åpne denne URL-en".
- **Forventet utdata** for hver kommando slik at brukeren vet når det gikk bra.
- **Feilsøking** for de 3–5 mest sannsynlige feilene, med eksakt feilmelding og fiks.
- **Avslutt med én setning om hva neste steg er** – aldri "gi meg beskjed når du er klar", men "neste leveranse er X, skal jeg fortsette?".

### Hva som *aldri* skal skje

- Brukeren skal aldri måtte gjette hvilken katalog en fil hører hjemme i.
- Brukeren skal aldri måtte fylle inn placeholder-tekst som `<YOUR_TENANT_ID>` uten at det er tydelig merket og forklart.
- Brukeren skal aldri måtte kjøre en fix-kommando som ikke var varslet.
- Brukeren skal aldri få en deploy-skript som lar ressurser ligge igjen og koste.
- Brukeren skal aldri måtte lese gjennom et langt svar for å finne kommandoen han skal kjøre – den skal stå fremhevet.

### Validering før du sier "ferdig"

Før du avslutter en leveranse, bekreft eksplisitt til brukeren:

- [ ] Alle filer har full sti og komplett innhold
- [ ] `docker compose up` starter alt lokalt
- [ ] `dotnet test` / `pytest` grønt på ferske tester
- [ ] README dekker kjør-lokalt og kjør-tester
- [ ] Eksempel-output for Drivdal-februar er inkludert
- [ ] Neste steg er tydelig kommunisert

Hvis noen av disse ikke er oppfylt, si det eksplisitt i svaret og foreslå hvordan hullet tettes – aldri skjul det.

---

## Kilder og videre lesning

Følgende bør studeres før du designer beregningsmodulene. Inkluder relevante sitater som kommentarer i koden der formler brukes.

Sources:
- [IEEE 762 – Standard Definitions for Electric Generating Unit Reliability, Availability, and Productivity](https://standards.ieee.org/ieee/762/6856/)
- [IEEE 762-2023 (gjeldende revisjon)](https://ieeexplore.ieee.org/document/10219445)
- [NERC GADS 2023 Data Reporting Instructions – Appendix F: Equations](https://www.nerc.com/globalassets/programs/rapa/gads/conventional/appendix_f_equations_2023_dri.pdf)
- [PJM Manual 22: Generator Resource Performance Indices](https://www.pjm.com/-/media/DotCom/documents/manuals/m22.ashx)
- [Availability factor – Wikipedia (rask referanse for EAF/EFOR)](https://en.wikipedia.org/wiki/Availability_factor)
- [NREL – Recommended key performance indicators for operational management](https://docs.nrel.gov/docs/fy20osti/72373.pdf)
- [Performance evaluation and benchmarking for hydropower dispatching (ScienceDirect)](https://www.sciencedirect.com/science/article/abs/pii/S0957178724000730)
- [IEA – Hydropower Special Market Report](https://iea.blob.core.windows.net/assets/83ff8935-62dd-4150-80a8-c5001b740e21/HydropowerSpecialMarketReport.pdf)
- [IEA – Hydropower Data Explorer](https://www.iea.org/data-and-statistics/data-tools/hydropower-data-explorer)
- [NVE – Vannkraft og vannkraftdatabase](https://www.nve.no/energi/energisystem/vannkraft/)
- [NVE – Langsiktig kraftmarkedsanalyse 2023](https://publikasjoner.nve.no/rapport/2023/rapport2023_25.pdf)
- [Streamflow variability and optimal capacity of run-of-river hydropower plants](https://agupubs.onlinelibrary.wiley.com/doi/full/10.1029/2012WR012017)
