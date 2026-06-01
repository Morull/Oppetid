# Instruks til Claude Code — felt for normal årsproduksjon per anlegg

Liten, avgrenset oppgave. **Kjør etter at `NESTE-CHAT-FIKS-KAIA-OG-VAKTROI.md`
er ferdig og committet** — den berører de samme anleggs-filene.

---

## Mål

Legg til et nytt felt på anleggs-admin-fanen (`PlantAdmin.razor`,
`/plants/{plantId}/admin`) for **normal årsproduksjon i GWh** — forventet
produksjon i et gjennomsnittlig («normalt») år for anlegget.

## Hvorfor (intensjon — påvirker design, men implementeres senere)

Feltet skal senere brukes til to ting:

1. **Produksjon vs normalår.** Sammenligne faktisk produksjon i en periode mot
   normalåret, så drifts-leder ser om vi ligger over eller under et snittår.
2. **Fordeling av felleskostnad etter GWh-andel.** F.eks. samlet årlig
   vaktkostnad legges inn én gang for hele porteføljen og fordeles per anlegg
   som `vaktkost_anlegg = total_vaktkost × (anlegg_GWh / Σ alle_anlegg_GWh)`.
   I dag er vaktkostnaden hardkodet (`_aarligVaktKost = 360_000` i
   `VaktRoi.razor`).

**Denne instruksen implementerer kun selve feltet** — de to bruksområdene er
egne, senere oppgaver (se nederst). Men design feltet riktig for dem.

---

## Endringene

Følg **nøyaktig samme mønster som `KaiaAnnualFeeNok`** (lagt til på
`PlantRegistration` i KAIA-arbeidet) — entitet → skjema-bro → EF-mapping →
kontrakt → endepunkt → UI.

1. **Entitet:** nytt felt på
   `Infrastructure/Persistence/Entities/PlantRegistration.cs`, f.eks.
   `public double? NormalAarsproduksjonGwh { get; set; }`. **Nullbart** —
   `null` = ikke satt ennå (ikke alle anlegg har tall fra start).

2. **Skjema-bro:** idempotent `ALTER TABLE ... ADD COLUMN IF NOT EXISTS
   normal_aarsproduksjon_gwh double precision` i `DatabaseBootstrapper.cs`,
   samme blokk-mønster som `kaia_annual_fee_nok`.

3. **EF-mapping:** kolonne-mapping i `KraftverkDbContext.cs`.

4. **API:** legg feltet til i request-/response-kontrakten og oppdaterings-
   endepunktet som anleggs-admin bruker (samme endepunkt som lagrer navn/
   type/`InstalledCapacityMw`/tidssone/plan-avviks-terskel — finn det og
   utvid det).

5. **UI — `PlantAdmin.razor`:** nytt input-felt i admin-skjemaet (det øverste,
   se skjermbildet), plassert rett etter «Installert effekt (MW)».
   - Etikett: «Normal årsproduksjon (GWh)».
   - `MudNumericField<double?>`, 1 desimal.
   - Hjelpetekst: «Forventet produksjon i et gjennomsnittlig (normalt) år.
     Brukes til å sammenligne mot faktisk produksjon og til å fordele
     felleskostnader etter GWh-andel.»
   - Tom verdi er gyldig (lagres som `null`).

## Merknad om enhet

Feltet er i **GWh** (drifts-leders ønske). Resten av appen regner i **MWh**
(Elhub). Den fremtidige sammenlignings-koden må derfor konvertere
(1 GWh = 1000 MWh). Ikke gjør noen konvertering nå — bare lagre GWh som
oppgitt.

---

## Akseptkriterier

- [ ] `dotnet build` grønt.
- [ ] `normal_aarsproduksjon_gwh`-kolonnen finnes etter oppstart (skjema-bro).
- [ ] Anleggs-admin viser feltet, kan lagre en verdi og la det stå tomt.
- [ ] Verdien persisteres og leses tilbake korrekt via API-et.
- [ ] Eksisterende anlegg får `null` (ikke 0) til drifts-leder fyller inn.

## Senere / oppfølging (ikke i denne instruksen)

- **Produksjon vs normalår:** en indikator (f.eks. «X % av normalår») som
  sammenligner sum Elhub mot `NormalAarsproduksjonGwh`, pro-rata for delårs-
  perioder. Naturlig plass: anleggs-detaljside og/eller portefølje.
- **GWh-fordelt vaktkostnad:** krever i tillegg en portefølje-innstilling for
  «samlet årlig vaktkostnad», og at `VaktRoi.razor` bytter den hardkodede
  `_aarligVaktKost` mot den GWh-fordelte andelen per anlegg. Bør spesifiseres
  som egen instruks.
