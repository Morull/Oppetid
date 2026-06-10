# Spec: Vakt-ROI overløp v2 — counterfactual-overløp via tilsigsmodell

**Status:** Klar til implementasjon (2026-06-10)
**Erstatter/utvider:** docs/SPEC-VAKT-ROI-OVERLOP.md (v1, observert overløp)
**Koordinering:** VaktRoiCalculator endres også av pågående ubalanse-fix (branch `featu`). Denne specen kjøres ETTER at ubalanse-arbeidet er landet — begge endrer samme fil og tester.

## Bakgrunn og problem

V1-regelen krediterer «Reddet produksjon» kun for timer med **observert** overløp i den faktiske perioden. Det er systematisk skjevt: i den faktiske perioden fikset vakta feilen (f.eks. etter 3 t) og turbinen kjørte videre og tappet magasinet. I counterfactual-scenariet står turbinen i hele vinduet (f.eks. 54 t) og magasinet fylles. **Jo bedre vakta gjør jobben, desto mindre observert overløp, desto mindre kreditt får den.** Observert overløp er bare en nedre grense.

Konkret case (Drivdal-type trip 2026-04-10 22:00, midt i snøsmelting): 3 t faktisk nedetid, 54 t counterfactual, 0 observerte overløpstimer → «vannet ville vært magasinert» og Reddet produksjon = 0. Med tilsig i april ville magasinet trolig gått fullt i løpet av vinduet.

Driftsleders premiss fra v1 («taper ikke penger om vi har plass i magasinet») er riktig — men spørsmålet er om det var plass i **54 timer**, ikke om det rant over mens turbinen kjørte.

## Løsning

`InflowOverflowEstimator` (Modules.Reporting/Nedetid/InflowOverflowEstimator.cs) implementerer allerede riktig modell og er i drift bak `GET /api/v1/plants/{plantId}/inflow-estimate` (InflowEstimateEndpoints + InflowOverflowQueryService). XML-doc-en på query-tjenesten sier eksplisitt at den er bygget «før vi ev. bytter Vakt-ROI til å bruke modellen primært». Dette er det byttet — som **tillegg**, ikke erstatning:

```
counterfactual_overlopstimer(gruppe) =
    observerte_overlopstimer                      (nedre grense — flommet det
                                                   over selv med turbin i drift,
                                                   flommer det i counterfactual)
  ∪ estimerte_timer fra InflowOverflowEstimator   (timer h ≥ leaderStart + hoursToFull)
```

Per leder-gruppe i VaktRoiCalculator:
- Estimat-vindu = [leaderStart, counterfactualEnd)
- Lookback = 24 t før leaderStart (eksisterende `LookbackHours`)
- Fyllgrad = siste sample før leaderStart (eksisterende logikk i InflowOverflowQueryService)
- Estimerte overløpstimer = hele klokketimer h (gulv-kvantisert, konsistent med eksisterende) der `h ≥ leaderStart + hoursToFull`
- Manuelle overrides (HaddeOverlop/IkkeOverlop) trumfer fortsatt alt — uendret

Estimatoren brukes KUN når terminal-dammen har ReservoirVolume- og TotalDamFlow-tagger og `VolumeMm3` er satt (samme krav som InflowOverflowQueryService). Anlegg uten magasin-telemetri (LevelProxy/ProductionStateProxy-modus) beholder dagens oppførsel uendret.

## Endringer

### 1. VaktRoiCalculator — nytt input og merge-logikk

Behold kalkulatoren ren/synkron. To alternativer, implementér A hvis ikke noe taler imot:

**A (anbefalt):** `Beregn(...)` får nye valgfrie parametre:
- `IReadOnlyList<InflowOverflowEstimator.HourlySample>? damSamples` — timepivoterte samples for [fromUtc − 24 t, maks counterfactualEnd], hentet én gang av kalleren
- `double maxVolumeM3` (0 = estimat ikke tilgjengelig)
- `IReadOnlyDictionary<DateTimeOffset, double>? fillRateByHour` (ratio 0..1)

Kalkulatoren kjører `InflowOverflowEstimator.Estimate` per leder-gruppe med slicet lookback (samples < leaderStart) og fyllgrad fra siste time før leaderStart. I time-løkka (dagens linje ~286): krediter produksjon hvis `overflowHours.Contains(h) || (estimat.DataAvailable && h >= leaderStart + estimat.HoursToFull-timer)`.

**B (alternativ):** to-fase — eksponer `BeregnLederVinduer(events, opsjoner)` som returnerer (PlantId, LeaderStart, CounterfactualEnd) per gruppe; endpoint henter estimat per vindu via InflowOverflowQueryService og sender `estimatByLeaderStart` inn i `Beregn`. Velg B hvis A gjør samples-plumbingen for klønete i portefølje-stien.

### 2. Nye felter i VaktRoiResultat (Core/Domain/VaktRoiResultat.cs)

- `SavedOverflowHoursObserved` (int) — fra SCADA-settet (dagens tall)
- `SavedOverflowHoursEstimated` (int) — tillegg fra estimatoren
- `OverflowEstimateAvailable` (bool)
- `OverflowEstimateHoursToFull` (double?, null = aldri fullt eller utilgjengelig)
- `OverflowEstimateForklaring` (string?) — estimatorens Forklaring-streng (tilsig, ledig kapasitet, tid til fullt)
- Eksisterende `SavedOverflowHours` = Observed + Estimated (bakoverkompatibelt)

`BuildForklaring` utvides: dagens tekst «ingen overløp i perioden — vannet ville vært magasinert» erstattes med estimatorens regnskap når estimat finnes, f.eks.: «Observert overløp: 0 t. Tilsigsmodell: snitt-tilsig X m³/s, ledig Y Mill.m³ ved hendelsesstart → fullt etter Z t av W t vindu → estimert N overløpstimer.»

### 3. Wiring — begge stier

- `NedetidEndpoints.GetVaktRoiAsync`: hent dam-samples/maxVolume/fyllgrad (gjenbruk logikken i InflowOverflowQueryService — trekk pivot-koden ut i en delt hjelper i stedet for å duplisere) og send inn i kalkulatoren.
- `PortfolioVaktRoiQueryService`: samme per anlegg.
- `InflowEstimateEndpoints` beholdes uendret (sammenligningsverktøyet er fortsatt nyttig).

### 4. UI

- `NedetidEventDetailDialog.razor` / VaktRoi-detalj: feltet «Overløps-timer (counterfactual)» viser i dag observerte timer under misvisende etikett. Vis nå: «Overløp observert: N t», «Overløp estimert (tilsigsmodell): M t», og estimat-forklaringen som hjelpetekst. Marker estimerte timer visuelt som estimat (f.eks. «~»-prefiks, samme konvensjon som andre estimater).
- «Reddet produksjon» som helt eller delvis bygger på estimat merkes «~» med tooltip om tilsigsmodellen.
- Behold override-kontrollene uendret — de er driftsleders fasit.

### 5. Usikkerhet — dokumentert begrensning, ikke blokker

Antakelsen «tilsig = konstant lookback-snitt» er svak over lange vinduer, særlig i flomperioder (da underestimeres tilsiget → estimatet er konservativt i riktig retning) og ved regnvær som starter i vinduet. Dette dokumenteres i XML-doc og i UI-tooltip. Ingen sikkerhetsfaktor i v2 — estimatet flagges som estimat, og override er korreksjonsmekanismen. (Vurder i v3: tilsig fra NVE/Sildre-API i stedet for lookback.)

## Tester

1. **Enhetstester VaktRoiCalculator** (utvid eksisterende VaktRoiCalculatorTests):
   - Gruppe uten observert overløp, estimat sier fullt etter 10 t av 54 t vindu → produksjonskreditt for timer 10–54 minus outage-timer; ubalanse-komponent uendret.
   - Estimat DataAvailable=false → identisk med dagens oppførsel (kun observert) + OverflowEstimateAvailable=false.
   - Observert ∪ estimert overlapper → ingen dobbelttelling av timer.
   - Override HaddeOverlop/IkkeOverlop trumfer estimat.
   - netto_inn ≤ 0 → 0 estimerte timer; fyllgrad mangler → DataAvailable=false.
   - Monoton-invarianten (bredere vindu redder aldri mindre) holder fortsatt med estimat-komponenten.
2. **Regresjonscase:** skjermbilde-caset 2026-04-10 (3 t outage, 54 t vindu) med syntetiske april-samples — verifiser at Reddet produksjon > 0 når modellen sier fullt før vindu-slutt.
3. Eksisterende InflowOverflowEstimator-tester beholdes; legg til test for time-kvantiseringen av hoursToFull-grensen.

## Akseptansekriterier

1. Hendelse med 0 observerte overløpstimer men fullt magasin i modellen får produksjonskreditt for timene etter hoursToFull.
2. Anlegg uten magasin-tagger/VolumeMm3: identisk oppførsel som i dag, ingen nye flagg satt.
3. UI skiller observert og estimert overløp, og estimat-baserte NOK-tall er merket som estimat.
4. Overrides fungerer som før og trumfer estimatet.
5. Alle eksisterende tester grønne; nye tester over på plass.
6. `SavedOverflowHours` i API-kontrakten er fortsatt sum (ingen breaking change for eksisterende konsumenter).
