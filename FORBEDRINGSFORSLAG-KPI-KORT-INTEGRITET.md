# Forbedringsforslag — KPI-kort-integritet

**Dato:** 2026-05-22
**Bakgrunn:** KPI-kort som står sammen i en visuell rad antyder en sammenheng. Hvis tallene *ser ut til* å høre sammen — særlig å summere — men ikke gjør det, feilleser brukeren dem. Skrevet for å gis videre til Claude Code, både som byggekrav for Produksjon-fanen og som retting av et konkret funn på Rapport-detalj.
**Revisjon utført:** Live gjennomgang av KPI-kortene på `/effektivitet` og `/reports/...` (Vikeså, april 2026), med kontroll av aritmetikken kort for kort.

---

## Del 1 — Regelen (byggekrav, ikke bare en test)

Hver gruppe KPI-kort skal oppfylle **én** av disse:

**(a) Reelle addender.** Kortene er komponenter som avstemmes mot en vist totalsum. Totalen vises eksplisitt, og komponentene går opp i den (innenfor avrunding).

**(b) Merket som selvstendige.** Kortene er uavhengige indikatorer og bærer en seksjonstittel som sier hva gruppen er (referanse vs. resultat, drift vs. økonomi, e.l.), slik at ingen forventer at de summerer.

I tillegg, uansett (a) eller (b):

- Et kort med ordet **«Sum»** eller **«Total»** skal aldri stå inntil ikke-komponent-kort i samme enhet. Da inviterer layouten til å legge sammen nabokortene og forvente totalen.
- Et kort som *er* en sum av navngitte deler bør vise delene (slik `KAIA-kostnad` gjør i dag — se Del 3).
- Kort med ulike enheter (t, %, MWh, NOK) i samme rad uten seksjonstittel er et varsel: enten gruppér dem, eller gi raden en tittel.

---

## Del 2 — Funn: Rapport-detalj bryter regelen

KPI-kortene øverst på rapporten er **9 kort i én flat blokk uten en eneste seksjonstittel**, og de blander fire enhetsfamilier:

| Kort | Verdi | Enhet |
|---|---|---|
| Drift-timer | 615 t | timer |
| Nedetid | 5 t | timer |
| Tilgjengelighet | 99,2 % | prosent |
| Bud-leveranse | 100,5 % | prosent |
| Total produksjon | 1739,75 MWh | energi |
| Spotomsetning | 1 857 498 NOK | kroner |
| Ubalansekost | −13 148 NOK | kroner |
| Oppgjør | 1 899 623 NOK | kroner |
| KAIA-kostnad | 1 198,64 kr | kroner |

### Det konkrete bruddet: «Oppgjør» går ikke opp

Kortet **«Oppgjør 1 899 623 NOK»** har undertittel «Sum oppgjør i perioden» og står i samme blokk, samme enhet, rett ved siden av:

- Spotomsetning: **1 857 498 NOK**
- Ubalansekost: **−13 148 NOK**

En leser vil legge sammen de to og forvente Oppgjør:

```
1 857 498 + (−13 148) = 1 844 350 NOK
Oppgjør viser:            1 899 623 NOK
Avvik:                       55 273 NOK  ← uforklart
```

Tallene *ser ut til* å høre sammen — alle i NOK, naborkort, ett merket «Sum» — men gjør det ikke. Dette er nøyaktig feilen regelen skal hindre.

**Merk:** dette er sannsynligvis ikke en regnefeil. «Oppgjør» kan med rette inneholde oppgjørslinjer som ikke vises som egne kort. Men da er *presentasjonen* feil: en sum som ikke avstemmes mot de synlige nabokortene, må enten (a) vise alle komponentene sine, eller (b) skilles ut visuelt og merkes som en selvstendig figur. Slik det står nå, leses den feil.

### Sekundært: ingen seksjonstitler

Hele 9-kort-blokken mangler inndeling. Drift (timer, tilgjengelighet, produksjon) og økonomi (NOK-kortene) står likestilt. Til sammenligning har KPI-katalog-**tabellen** lenger ned på samme side allerede seksjonene «Drift», «Marked» og «Økonomi» — den inndelingen bør speiles på kort-blokken øverst.

---

## Del 3 — Funn: gode eksempler å kopiere (samme side)

To kort på Rapport-detalj gjør det riktig og bør brukes som mal:

- **KAIA-kostnad — 1 198,64 kr**, undertittel «Megler 869,87 kr · Fast 328,77 kr». Kontroll: 869,87 + 328,77 = 1 198,64. ✓ En ekte addender-figur som viser delene sine — mønster (a).
- **Tilgjengelighet — 99,2 %**, undertittel «Drift-timer / (Drift + Nedetid)». Kontroll: 615 / (615 + 5) = 99,2 %. ✓ Viser formelen sin, så tallet kan etterprøves direkte.

Lærdommen: et kort som har en relasjon til andre tall, skal vise relasjonen — enten som formel eller som navngitte komponenter.

---

## Del 4 — Funn: Effektivitet er stort sett i orden

KPI-kortene på `/effektivitet` er delt i **tre grupper, hver med egen seksjonstittel** — mønster (b) er implementert:

| Seksjon | Kort |
|---|---|
| Effektivitet | Snitt η · Sweet-spot effekt · Spesifikt vannforbruk · Total produksjon |
| Økonomi | Capture rate · Merverdi vs spot |
| Underytelse — tapstoppliste | Total tapt verdi · Antall episoder · Snitt-Δη i episoder |

Ingen avstemmingsbrudd funnet. Kontrollert: «Antall episoder 51 / 64 intervaller under terskel» er internt konsistent (episoder grupperer sammenhengende intervaller, 51 ≤ 64). ✓

Ett mindre punkt: i «Effektivitet»-gruppen står **«Total produksjon»** sammen med tre kort som ikke er komponenter av den (Snitt η, Sweet-spot, Spesifikt vannforbruk). Ordet «Total» kan antyde at naboene summerer til den. Lav alvorlighet — ulike enheter demper feillesingen — men vurder å droppe «Total» (f.eks. «Produksjon i perioden»).

---

## Del 5 — Spesielt for Produksjon-fanen

Produksjon-fanen er under bygging. Når KPI-kortene der settes opp på ekte:

- Følg regelen i Del 1 fra start — det er billigere enn å rette etterpå.
- Hvis fanen viser plan/spotbud/faktisk som tall: avklar om de skal avstemmes (faktisk − plan = avvik, vist eksplisitt) eller stå som selvstendige målinger under en seksjonstittel. Ikke la dem stå som en udelt rad.
- Gjenbruk «Drift / Marked / Økonomi»-inndelingen fra KPI-katalogen.

---

## Del 6 — Slik verifiseres det (test-steg)

KPI-kort-integritet fanges ikke av en smoke-test — et kort som ikke går opp kaster ingen feil og svarer HTTP 200. Det krever en egen sjekk:

1. Hent ut hvert korts etikett, verdi, enhet og undertittel fra DOM-en.
2. For hver gruppe i samme enhet: finnes et «Sum»/«Total»-kort? Går nabokortene opp i det, innenfor avrunding?
3. For hvert kort med formel eller navngitte deler i undertittelen: stemmer regnestykket?
4. Står kort med ulike enheter i samme rad uten seksjonstittel? Flagg.

Punkt 2–3 kan etterprøves automatisk så snart kortverdiene er hentet. Punkt 4 er en strukturkontroll. Sjekken bør kjøres på Produksjon når fanen er ferdig bygd (den lå i lastetilstand under denne revisjonen — sannsynlig work-in-progress).

---

## Oppsummering

| Side | Status | Tiltak |
|---|---|---|
| Rapport-detalj | **Brudd** — «Oppgjør» avstemmer ikke mot synlige nabokort; ingen seksjonstitler | Vis Oppgjørs komponenter, eller skill kortet ut og merk det. Legg til Drift/Marked/Økonomi-seksjoner. |
| Effektivitet | OK — tre titlede grupper | Vurder å fjerne «Total» fra «Total produksjon». |
| Produksjon | Ikke bygd / lå i lasting | Følg regelen ved bygging; kjør verifisering når ferdig. |
