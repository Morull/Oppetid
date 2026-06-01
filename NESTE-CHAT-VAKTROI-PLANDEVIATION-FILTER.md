# NESTE CHAT — Vakt-ROI: filtrer ut U2-PlanDeviation-hendelser uten operlog-match

**Dato:** 2026-05-22
**Skrevet for:** Claude Code, etter avklaring med drifts-leder.
**Avhengighet:** Bygger på `VaktEventOverrideEntry`-mønsteret (Classification + B1-`ActualEndOverrideUtc`). Krever ikke at vindu-bugen i `NESTE-CHAT-VAKTROI-OG-UI-FIKS.md` Del A er fikset først, men bør commitres etter den for å unngå merge-konflikt i `VaktRoi.razor`.

---

## Bakgrunn

I dag flagger Vakt-ROI hver nedetidshendelse med `erReddbar=true` så lenge den ligger innenfor vakt-vinduet og har positive ekstra-timer. Det fanger også **U2-PlanDeviation**-hendelser — automatisk-detekterte avvik mellom Elhub og produksjonsplan — som i de fleste tilfeller løses av SCADA uten at vakta utrykker. Operatørene merker som regel kun feil når det kommer en alarm i SCADA; uten alarm logges det ikke. Resultatet er at ROI-grunnlaget overvurderer hva vaktordningen faktisk har reddet.

Drifts-leders vurdering: alarm-trigger (i praksis = en oppføring i operatør-loggen) skal være forutsetning for at en U2-hendelse teller som vakt-utrykning, og drifts-leder skal kunne overstyre per hendelse hvis den automatiske vurderingen tar feil.

## Beslutninger (avklart 2026-05-22)

| Spørsmål | Beslutning |
|---|---|
| Trigger-signal | **Eksisterende `harOperlogMatch`**-flagg per hendelse. Ingen ny signal-mapping nødvendig. |
| Default for eksisterende hendelser | **`Auto`** — historiske ROI-tall vil endre seg. Akseptert engangskostnad; nevn i commit-meldingen. |
| Omfang | **Kun `causeCode = "U2-PlanDeviation"`**. Andre kategorier (U1-UnplannedStop, PlanlagtVedlikehold, TettInntaksrist osv.) er uendret. |

## Logikken som skal implementeres

For hver hendelse, før eksisterende vindu-/timer-/ROI-beregning:

```
EffectiveGuardResponse(event, override):
    if event.causeCode != "U2-PlanDeviation":
        return true                              // uendret — andre kategorier teller som i dag
    if override.GuardResponseOverride == Yes:
        return true                              // drifts-leder bekreftet
    if override.GuardResponseOverride == No:
        return false                             // drifts-leder avkreftet
    return event.harOperlogMatch                 // Auto / null → trigger-betingelsen
```

Hendelser hvor `EffectiveGuardResponse == false`:
- `erReddbar = false`
- `reddetMwh = reddetNok = reddetProduksjon_NOK = reddetUbalanse_NOK = 0`
- `ekstraTimerSpart = 0`
- Hendelsen vises fortsatt i nedetid-loggen og i Vakt-ROI-tabellen, men teller ikke i ROI-summen.

---

## Endringene

### 1. Datamodell

Utvid `Infrastructure/Persistence/Entities/VaktEventOverrideEntry.cs`:

```csharp
public GuardResponseOverride GuardResponseOverride { get; set; } = GuardResponseOverride.Auto;
```

Ny enum (egen fil i `Core/Domain/VaktRoi/`):

```csharp
public enum GuardResponseOverride
{
    Auto = 0,    // bruk EffectiveGuardResponse-logikken (operlog-match for U2, true ellers)
    Yes  = 1,    // drifts-leder bekrefter at vakt rykket ut
    No   = 2,    // drifts-leder bekrefter at vakt IKKE rykket ut
}
```

Idempotent skjema-bro i `DatabaseBootstrapper.cs`, samme blokk-mønster som `actual_end_override_utc` (B1):

```sql
ALTER TABLE core.vakt_event_overrides
ADD COLUMN IF NOT EXISTS guard_response_override smallint NOT NULL DEFAULT 0;
```

EF-mapping i `KraftverkDbContext.cs`. Default 0 = `Auto`, så eksisterende rader får riktig verdi uten data-migrering.

### 2. Kontrakt og endepunkt

Utvid `UpsertVaktOverrideRequest` / `VaktOverrideDto` i `VaktOverrideEndpoints.cs` med:

```csharp
public GuardResponseOverride? GuardResponseOverride { get; init; }
```

I `UpsertAsync`: behold null som «ikke endre», samme mønster som for `Classification` og `ActualEndOverrideUtc`. En override-rad bærer nå opptil tre uavhengige overstyringer (klassifisering, varighet, vakt-utrykning).

### 3. Calculator-/query-tjenester

To steder må filtreres oppstrøms for `VaktRoiCalculator`, slik at kalkulatoren selv forblir en ren funksjon:

- **Per anlegg:** `NedetidEndpoints.GetVaktRoiAsync` — der events kombineres med overrides.
- **Portefølje:** `PortfolioVaktRoiQueryService` — samme sted som B1-overstyringen for varighet anvendes.

I begge: før kalkulatoren kalles, sett `reddetNok/Mwh = 0`, `erReddbar = false`, `ekstraTimerSpart = 0` for hver hendelse hvor `EffectiveGuardResponse == false`. Da boobler 0-bidraget naturlig gjennom `totalReddetNok` og `antallReddbareInnenforVakt`.

### 4. UI — `VaktRoi.razor`

**Ny kolonne i hendelsestabellen:** «Vakt utrykt» med verdier:

| Auto + operlog | «Auto · alarm trigget» (grønn prikk) |
| Auto + ikke operlog | «Auto · ingen alarm» (grå prikk) — kun for U2 |
| Yes | «Ja» (drifts-leder bekreftet) |
| No | «Nei» (drifts-leder avkreftet) |

For ikke-U2-hendelser: vis bare «Auto · alltid teller» (grå) — feltet er informasjonsmessig der.

**Detaljer-popup (B3):** Legg til en seksjon «Vakt-utrykning»:

- Radio: `Auto` / `Ja, vakt rykket ut` / `Nei, ingen utrykning`.
- Info-linje under: «Operatør-logg-match i tidsvinduet: ja/nei». Hvis ja, vis tidsstempel for første match.
- Lagre via samme `PUT .../vakt-overrides` som B1/Classification.

**Ny seksjon på Vakt-ROI (per anlegg):** «Hendelser til vurdering»

Plassering: rett under den eksisterende hendelses-tabellen, kollapsbar. Innhold:

- Hendelser hvor `causeCode = "U2-PlanDeviation"` AND `GuardResponseOverride = Auto` AND `harOperlogMatch = false`.
- Kolonner: Start, Slutt, Kategori (alltid TripFeil her), Årsak (`U2-PlanDeviation` + brukervennlig tekst), Tap (MWh), Tap (NOK), og en handlingsknapp «Vurder» som åpner Detaljer-popup-en med fokus på Vakt-utrykning-seksjonen.
- Tellerverdi i seksjonsoverskriften: «Hendelser til vurdering: N» — synlig selv når kollapset. Drifts-leder ser dermed straks hvor mange ubekreftede saker som ligger og venter.
- Disse er ekskludert fra ROI nå — det er meningen. Når drifts-leder velger Yes/No telles de eller forblir ute permanent.

---

## Tester

Utvid `VaktRoiCalculator`-tester med matrisen av `causeCode × GuardResponseOverride × harOperlogMatch`:

| causeCode | Override | harOperlogMatch | Forventet reddet |
|---|---|---|---|
| U2-PlanDeviation | Auto | false | 0 |
| U2-PlanDeviation | Auto | true | som i dag |
| U2-PlanDeviation | Yes | false | som i dag (overstyring vinner) |
| U2-PlanDeviation | Yes | true | som i dag |
| U2-PlanDeviation | No | false | 0 |
| U2-PlanDeviation | No | true | 0 (overstyring vinner) |
| U1-UnplannedStop | Auto | false | som i dag (ikke berørt) |
| U1-UnplannedStop | Auto | true | som i dag |

---

## Akseptkriterier

- [ ] `core.vakt_event_overrides` har kolonnen `guard_response_override` (smallint, default 0) etter oppstart.
- [ ] `dotnet build` grønt; alle eksisterende `VaktRoiCalculator`-tester passerer.
- [ ] Nye tester for matrisen over passerer.
- [ ] `PUT .../vakt-overrides` godtar `guardResponseOverride: "Auto"|"Yes"|"No"`.
- [ ] For U2-PlanDeviation uten operlog-match og override=Auto: hendelsen vises i tabellen, men bidrar med 0 til `totalReddetNok` og inngår ikke i `antallReddbareInnenforVakt`.
- [ ] Ny kolonne «Vakt utrykt» vises i hendelsestabellen med riktig status per hendelse.
- [ ] Detaljer-popup kan endre vakt-utrykning til Auto/Ja/Nei og endringen reflekteres i totalsummen ved reload.
- [ ] «Hendelser til vurdering»-seksjonen viser teller og kan ekspandere/kollapse.
- [ ] Ikke-U2-hendelser har uendret oppførsel (regresjons-sjekk mot en kjent rapport).

## Merknad om historikk

Historiske `totalReddetNok`-tall vil endre seg etter at fiksen rulles ut, fordi U2-hendelser uten operlog-match faller ut av summen. Det er bevisst og avklart. Nevn i commit-meldingen, og vurder å føre opp i en kort «endringer i ROI-grunnlaget»-notis (changelog eller drifts-leders runbook) slik at forskjellen mellom gamle og nye tall kan forklares hvis noen spør.
