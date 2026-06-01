# Instruks til Claude Code — hendelsesdetaljer på Nedetid-siden

Liten, **ren frontend-oppgave** (`Nedetid.razor`). Søster-oppgave til
per-hendelse-popup-en i Vakt-ROI (Del B3 i `NESTE-CHAT-FIKS-KAIA-OG-VAKTROI.md`).

**Kjør etter at Vakt-ROI-popup-en (B3) er ferdig og committet** — da kan du
gjenbruke samme dialog-mønster, så de to sidene får konsistent uttrykk.

---

## Mål

På Nedetid-fanen sin «Hendelser»-tabell skal drifts-leder kunne åpne en
**detalj-popup per hendelse**, på samme måte som popup-en under «Hendelser med
ROI-vurdering» i Vakt-ROI. I dag har Nedetid-tabellen bare en blyant-knapp for
annotering — drifts-leder vil ha en samlet detaljvisning.

## Viktig — dataene finnes allerede

`NedetidEventDto` (radene i `_response.Events`) har alt som trengs:
`StartUtc`, `EndUtc`, `VarighetTimer`, `Kategori`, `CauseCode`, `State`,
`TapMwh`, `TapNok`, `HarOperlogMatch`. Ingen API-/DB-endring trengs — dette er
ren frontend.

---

## Endringene (alt i `Nedetid.razor`)

Legg til en **«Detaljer»-knapp** (eller gjør raden klikkbar) per rad i
«Hendelser»-tabellen, som åpner en `MudDialog`. Følg samme dialog-mønster som
Vakt-ROI-popup-en (B3) og `Components/AnnotationDialog.razor`, så de to sidene
ser like ut.

Popup-en skal samle for én hendelse:

- **Hendelsesinfo:** start, slutt (lokal tid), varighet, kategori, tilstand
  (`State`).
- **Årsak:** brukervennlig tekst via `CauseFormatter` *og* den rå
  `CauseCode`-en under (slik at drifts-leder ser nøyaktig hva SCADA/operlog
  rapporterte — nyttig for å slå opp i SCADA og for å vurdere nye cause-alias).
- **Operlog:** om hendelsen har operlog-match (`HarOperlogMatch`).
- **Økonomi:** Tap (MWh) og Tap (NOK).
- **Annotering:** vis eksisterende annotering hvis den finnes, og en knapp for
  å annotere/redigere. Gjenbruk den eksisterende `AnnotationDialog`-flyten som
  blyant-knappen bruker i dag — enten ved å åpne den derfra, eller ved å
  integrere den. Ikke dupliser annoterings-logikken.

Den eksisterende blyant-/«Rediger»-knappen kan beholdes eller erstattes av
«Detaljer»-knappen som hoved-inngang — velg det som gir ryddigst tabell, men
annoterings-funksjonen må fortsatt være tilgjengelig fra popup-en.

## Akseptkriterier

- [ ] `dotnet build` grønt.
- [ ] Hver rad i Nedetid sin «Hendelser»-tabell kan åpne en detalj-popup.
- [ ] Popup-en viser full hendelsesinfo, årsak (vennlig + rå kode), operlog-
      status og tap (MWh + NOK).
- [ ] Annotering kan opprettes/redigeres fra popup-en (gjenbrukt logikk).
- [ ] Tidspunkt vises i lokal tid (Europe/Oslo).
- [ ] Popup-en ser konsistent ut med Vakt-ROI sin hendelses-popup.
- [ ] Ingen endringer i API, query-tjenester eller DB.

## Merknad

Ren frontend. All data ligger allerede i `NedetidEventDto`. Hold deg til
`Nedetid.razor` (+ gjenbruk av eksisterende dialog-komponenter).
