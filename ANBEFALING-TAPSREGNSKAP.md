# Anbefaling til Claude Code — Tapsregnskap

**Dato:** 2026-05-22
**Skrevet for:** Claude Code, basert på en gjennomgang av appen og en arbeidsøkt med drifts-leder.
**Status:** Konsept avklart med bruker. Klar til implementasjon i prioritert rekkefølge.

## Bakgrunn og mål

Appen (Oppetid / KraftverkUptime) har mange KPI-er — effektivitet, capture rate,
produksjon, nedetid, vakt-ROI — men de er spredt på hver sin side. Hver KPI er
diagnostisk alene, men ingen side svarer på spørsmålet drifts-leder faktisk
stiller: **hvor taper vi penger, rangert, og hva er verdt å gjøre noe med?**

Målet med appen er å utvikle anleggene slik at de tjener mer — på produksjon og
vedlikehold. Appen er bare god hvis den finner konkrete forbedringspunkter.

Denne anbefalingen beskriver én samlende idé — **tapsregnskapet** — og hva Code
skal bygge først.

## Ideen kort: tapsregnskapet

Alt koker ned til samme enhet: **tapte kroner mot et realistisk alternativ**, på
samme timesoppløste tidslinje. For hver time dekomponeres tapet i navngitte,
additive bøtter:

| Bøtte | Hva | Status i appen |
|---|---|---|
| Nedetid | Turbin sto når den burde kjørt | Finnes (Vakt-ROI / Nedetid) |
| Overløp | Vann rant forbi ubrukt | Finnes (overflow-modell) |
| Driftspunkt (η) | Kjørte dårlig for det effektnivået | Finnes (EffektivitetEpisodeService) |
| Disponering | Produserte i feil timer gitt pris | Delvis (Produksjon, capture rate) |
| Ubalanse | Avvik fra bud kostet regulerkraft | Finnes (ProduksjonAnalyseCalculator) |
| Rist-falltap | Tett inntaksrist stjeler fallhøyde | Signal finnes, ikke beregnet |

Når alt står i kroner på samme tidslinje kan man **summere** per time, **rangere**
perioder (mest tap øverst = jobb her først), **dekomponere** hver topp-periode
(hvilken bøtte dominerte), og **gruppere** for å finne gjentagelse.

To prinsipper er viktige:

- **Ranger på tapt NOK**, ikke på antall dårlige KPI-er. En time der bare overløp
  er galt men koster 200 000 kr betyr mer enn fem småting som summerer til 5 000.
- **Samtidighet er et rotårsak-signal.** Når flere bøtter lyser samtidig er det
  ofte én felles årsak (kommunikasjonsbrudd, frossen giver, operatør-override).
  Vis det som et flagg — én fiks, flere symptomer.

Et viktig skille for driftspunkt-bøtta: virkningsgrad måles mot **bin-snittet**
(η for andre intervaller på samme effekt), ikke mot sweet spot. Da straffes ikke
en bevisst beslutning om å kjøre høyt for å fange høy pris — bare reell, fiksbar
sløsing. "Skulle jeg kjørt da i det hele tatt" er et eget spørsmål som hører
hjemme i disponerings-bøtta.

`EffektivitetEpisodeService.cs` er allerede malen: den finner underytende
episoder, slår dem sammen og rangerer på tapt NOK. Tapsregnskapet generaliserer
akkurat det mønsteret til alle bøttene.

## Hva Code skal gjøre — prioritert

### 1. Produksjon-fanen: «Netto mot plan» (start her)

Dette er den klart høyest-verdi, lavest-risiko jobben. Dataene finnes allerede i
`ProduksjonAnalyseCalculator.cs` — dette er i hovedsak presentasjon.

**Problemet:** Produksjon-fanen viser «Hydrogrid merverdi» som et frittstående
tall. Det er verdien av *planens* timing — det sier ingenting om hva man faktisk
satt igjen med. I perioder med lavt plantreff kan merverdien se ut som en gevinst
mens perioden i virkeligheten gikk i tap, fordi ubalanse-kosten aldri kobles på.

**Løsningen — ett ærlig hovedtall:**

```
Planens timing-verdi   = HydrogridMerverdiNok   (finnes — egenskap ved planen)
Realisert timing-verdi = FaktiskMerverdiNok     (finnes — egenskap ved faktisk produksjon)
Timing-gap             = Realisert − Planens
Ubalanse-kost          = Σ UbalanseKostNok      (finnes, per time)
Netto mot plan         = Timing-gap + Ubalanse-kost
```

«Netto mot plan» er det ærlige svaret: kostet avviket fra planen oss penger denne
perioden. Følger man planen perfekt går alle ledd mot null.

**Konkret:**

- Døp om «Hydrogrid merverdi» → «Planens timing-verdi». Navnet «merverdi» leses
  som inntjening; det er det ikke.
- KPI-kortene må enten være ekte addender som går opp (Timing-gap + Ubalanse-kost
  = Netto mot plan), eller være tydelig merket som referanse kontra resultat.
  Ikke bland et referanse-tall inn i en sum.
- «Netto mot plan» er et *eksekverings*-resultat (timing + ubalanse). Volumavvik
  mot plan (mer/mindre vann tilgjengelig) er ikke en feil — vis det som en egen,
  nøytral linje, ikke som tap.

**Månedstrend-tabell:** Måneder som rader. Kolonnegruppe «Resultat mot plan»:
Planens timing-verdi, Realisert timing-verdi, Timing-gap, Ubalanse-kost, Netto
mot plan. Netto-kolonnen får en in-celle søyle skalert mot verste måned, så de
dårlige månedene springer ut. Rad klikkbar → drill-down.

**Drill-down:** Klikk en måned → timene som drev ubalanse-kosten, sortert etter
kroner. Kolonner: Tidspunkt, Forpliktelse (MWh), Levert, Underlevering, Spot,
RK-pris, Premium (RK−spot), Ubalanse-kost. Viser at kosten = underlevering ×
premium, og at den som regel er konsentrert i få timer.

Filer: `Produksjon.razor`, `ProduksjonAnalyseCalculator.cs` (legg til Netto mot
plan i resultat-recorden + per måned), `ProduksjonTimeline.razor` (valgfritt:
divergerende stripe timing vs ubalanse).

### 2. Ubalanse-formelen: gjør den tosidig

Dagens formel i `ProduksjonAnalyseCalculator.cs` straffer bare **underlevering**:
`premium × max(0, forpliktelse − Elhub)`. Men **overlevering** koster også —
overskuddet avregnes til nedregulerings-pris, som kan ligge under spot.

Gjør formelen tosidig hvis nedregulerings-pris finnes i avregningsdataene. Hvis
ikke: flagg det som en kjent begrensning i UI-et («kun underlevering medregnet»),
så «Netto mot plan» ikke leses som mer presist enn det er.

### 3. Rist-falltap: bygg metoden, men bak en datakvalitets-port

Rist-falltap er en egen bøtte — tett inntaksrist stjeler fallhøyde og dermed
produksjon. Den er fysisk atskilt fra driftspunkt-η: rista sitter *oppstrøms*
turbinen, så tap her vises ikke i turbinvirkningsgraden.

**Viktig funn fra økten:** falltap-signalet på Lindland
(`LINDLAND_INNTAK_RIST_FALLTAP_PV`) er **ikke pålitelig som det er** — det leser
~7,6 cm når anlegget står helt stille (skal være 0), har nær null korrelasjon med
vannføring², og går fysisk umulig negativt. Sannsynligvis en udisiplinert
differansemåling med drivende nullpunkt.

Derfor skal Code **ikke** vise et rist-falltap-tall ukritisk. Bygg:

1. **Datakvalitets-port** for falltap-signal: signalet må (a) lese ≈0 når
   vannføringen er ≈0, og (b) korrelere med Q². Består det ikke, skal bøtta si
   «kan ikke beregnes — falltap-signalet er ikke pålitelig», ikke vise et tall.
2. **Auto-nullstilling:** estimer offset fra perioder med Q ≈ 0 og trekk den fra
   (interpolert over tid).
3. **Beregningen** (klar — verifisert i økten): `Q_total` = sum av turbin-
   vannføring for alle generatorer; ren-rist baseline `k·Q²` kalibrert fra de
   reneste intervallene; mertap = `max(0, falltap − baseline)`; tapt effekt =
   `ρ·g·Q_total·mertap·η`; → MWh → NOK med timespris. Gate hele beregningen bak
   datakvalitets-porten.
4. Map signalet til `SignalRole.GridFallLoss` (i dag mappet som `Other` for
   flere anlegg).

Drifts-leder verifiserer selv giveren i SCADA. Code sin jobb er at metoden er
klar den dagen signalet er til å stole på.

## Retningen videre

Produksjon-fanen («Netto mot plan») er i praksis de to første bøttene —
disponering og ubalanse — gjort ekte på én fane. Når den står, generaliseres
mønsteret: de øvrige bøttene (nedetid, overløp, driftspunkt, rist-falltap) legges
inn i samme regnskap, i samme kroner, på samme tidslinje, med rangering og
samtidighets-flagg. Det er da appen svarer på «hvor taper vi mest».

Start smått: de tre «sikre» bøttene (nedetid, overløp, driftspunkt) trenger ingen
vannverdi-modell og kan forsvares hardt. Disponering er den vanskeligste — den
avhenger av at Hydrogrid-planen er god nok som referanse — og bør komme sist.

## Prinsipper å holde fast på

- Pure-funksjon-kalkulatorer skilt fra query-tjenester, og enhetstestet. Matcher
  eksisterende arkitektur (`EffektivitetEpisodeService`, `ProduksjonAnalyseCalculator`).
- KPI-kort: ekte addender som går opp, eller tydelig merket referanse vs. resultat.
- Ranger på tapt NOK. Samtidighet er rotårsak-signal, ikke utvelgelseskriterium.
- Hver bøtte har en datakvalitets-port. Bedre å si «kan ikke beregnes» enn å vise
  et falskt tall.

## Foreslått byggerekkefølge

1. Produksjon-fanen: «Netto mot plan» + omdøping + KPI-kort som går opp (punkt 1).
2. Tosidig ubalanse-formel (punkt 2).
3. Månedstrend-tabell + drill-down på Produksjon-fanen (punkt 1, forts.).
4. Rist-falltap: datakvalitets-port + beregning bak port (punkt 3).
5. Generaliser til fullt tapsregnskap — egen, mer detaljert spec når punkt 1–4 står.

Punkt 1–3 er avgrenset, bruker data som allerede beregnes, og gir umiddelbar
verdi. Punkt 4 er klart, men venter på at giveren verifiseres. Punkt 5 er det
store målet og bør spesifiseres separat når fundamentet er på plass.
