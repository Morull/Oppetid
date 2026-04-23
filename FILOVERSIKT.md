# Filoversikt – KraftverkUptime

Alle filer ligger i outputs-mappen på din maskin. Her er hva hver fil er og
hvilke du trenger for neste steg.

## Samlet nedlasting

`kraftverkuptime-alle-filer.zip` inneholder alt relevant i én pakke. Pakk ut
denne hvis du vil ha hele bibliotekket på ett sted utenfor Cowork-mappen.

## Struktur og formål

### Promptbibliotek (designdokumenter)
| Fil | Formål | Når brukes den |
|---|---|---|
| `prompt-1-plattform.md` | Plattform- og arkitektur-spec (.NET, Azure, kontrakter, sømmer) | Lim inn først i Steg 1-chat |
| `prompt-2-domene.md` | Hydro-domene-spec (KPI-er, klassifisering, parser) | Lim inn først i Steg 2-chat |
| `prompt-oppetidsanalyse-kraftverk.md` | Samlet referansedokument med full begrunnelse | Oppslag ved tvil, ikke til direkte bruk |
| `NESTE-CHAT-START.md` | Oppstartsmelding for ny chat | Lim inn som første melding i ny chat |

### Resultater fra Steg 0 (Python PoC på Drivdal)
| Fil | Formål |
|---|---|
| `drivdal-feb2025-uptime-report.xlsx` | Menneskelesbar rapport med KPI-er, klassifisering, 3-veis figur |
| `drivdal-feb2025-fasit.json` | **Regresjonsfasit** som .NET-implementasjonen skal matche |
| `drivdal-feb2025-summary.txt` | Rask tekstsammendrag |

### Python-implementasjonen (referanse for .NET)
Under `drivdal-analyse/`:
| Fil | Formål |
|---|---|
| `fixtures/drivdal-feb2025.xlsx` | **Testfixtur** – skal brukes som input i .NET-tester |
| `src/parser.py` | Referanse for `SettlementDataSource` i .NET |
| `src/quality.py` | Referanse for datakvalitets-logikk |
| `src/classifier.py` | Referanse for klassifiseringsregler |
| `src/kpi.py` | Referanse for KPI-formler |
| `src/report.py` | Referanse for Excel-rapport |
| `src/main.py` | Orchestrator |
| `output/*` | Duplikater av rapportene (fra Python-kjøringen) |

## Hva du trenger for Steg 1 (plattform)

Minimum:
- `NESTE-CHAT-START.md` (lim inn først)
- `prompt-1-plattform.md` (modellen vil lese den)

## Hva du trenger for Steg 2 (domene)

Når plattformen står:
- `prompt-2-domene.md`
- `drivdal-feb2025-fasit.json` (for regresjonstester)
- `drivdal-analyse/fixtures/drivdal-feb2025.xlsx` (testfixtur)
- `drivdal-analyse/src/*.py` (referanseimplementasjon)

## Hvis noe mangler i Cowork-mappen

Alle filer er kopiert til din valgte outputs-mappe. Hvis en fil mangler:
1. Sjekk at du er i riktig arbeidsmappe
2. Pakk ut `kraftverkuptime-alle-filer.zip` som har samme struktur
3. Kom tilbake hit og be om rekopi
