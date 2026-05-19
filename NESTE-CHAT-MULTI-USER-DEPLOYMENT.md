# Neste sesjon — Multi-user-deployment

**Dato opprettet:** 2026-05-03
**Spec:** `docs/SPEC-MULTI-USER-DEPLOYMENT.md`
**Estimat:** 3-5 dager arbeid (over 1-2 uker kalender)
**Bakgrunn:** I dag kjører appen lokalt på Mortens PC. Skal gjøres tilgjengelig for andre i Dalane Kraft.

## Mål

Få appen kjørende på Azure med:
- Microsoft Entra ID-pålogging for alle brukere i Dalane Kraft
- Reader vs Admin-roller
- Custom domene `uptime.dalanekraft.no` med HTTPS
- Automatisk backup og overvåkning
- CI/CD via GitHub Actions
- ~950 NOK/mnd i drift

## Forutsetninger som må avklares FØR koding starter

| Punkt | Hvem |
|---|---|
| Azure-abonnement og kostnadssted godkjent | Ledelse + IT |
| Domenenavn (`uptime.dalanekraft.no` eller annet) | IT/DNS-administrator |
| Entra ID-administrator-tilgang for app-registrering | IT |
| Liste over brukere som skal ha tilgang + roller | Drifts-leder bekrefter |
| Bekreft at MVP-hardening-spec er implementert | (ferdig per OVERLEVERING-2026-05-03) |

**Stopp-policy:** ikke start fase 1 før alle disse er avklart. Krever bruker-koordinasjon, ikke teknisk arbeid.

## Implementasjons-faser

| Fase | Innhold | Estimat |
|---|---|---|
| 0 | Avklaringer (Azure-konto, domene, Entra-admin) | 1-2 dager kalender |
| 1 | Azure-infrastruktur via Azure CLI | 4-6 t |
| 2 | Entra ID app-registrering + roller + brukertildeling | 2-3 t |
| 3 | Custom domain + HTTPS-sertifikat | 1-2 t |
| 4 | GitHub Actions CI/CD | 2-3 t |
| 5 | Auto-import hot folder via Azure File Share (kan utsettes) | 3-4 t |
| 6 | Backup-konfig + restore-test | 1-2 t |
| 7 | Application Insights + alerts | 2-3 t |
| 8 | Brukerguide + onboardings-mail | 1-2 t |

## Bekreftede design-valg

| Valg | Beslutning |
|---|---|
| Hosting | Azure App Service (B1 Linux) — anbefalt fremfor on-prem |
| Database | Azure Database for PostgreSQL Flexible (B1ms) |
| Storage | Azure Blob + Azure File Share for hot folder |
| Region | Norway East (lavest latens for Sokndal) |
| Auth | Microsoft Entra ID med Reader/Admin-roller |
| Secrets | Azure Key Vault, hentes via managed identity |
| CI/CD | GitHub Actions med staging-slot for null nedetid |
| Backup | 14-dagers Postgres + 90-dagers blob soft delete |
| Domene | `uptime.dalanekraft.no` (CNAME til Azure Web App) |
| HTTPS | App Service-administrert sertifikat (gratis) |

## Spørre-policy

- Hvis Azure-abonnement eller domene ikke er på plass: STOPP og rapporter behov til bruker, ikke prøv å improvisere
- Hvis Entra-rolletildeling feiler: rapporter Azure-portal-feilmelding direkte
- Hvis Postgres-migrasjon mot Azure-DB feiler: stopp og verifiser SSL-konfig før retry
- Hvis CI/CD-deploy-test mislykkes: verifiser publish-profile i GitHub-secrets, ikke debug i prod

## Sikkerhets-sjekkliste før go-live

- [ ] Alle endepunkter krever auth (verifisert via curl-test)
- [ ] Admin-endepunkter krever Admin-rolle
- [ ] Audit-logg fungerer
- [ ] Postgres bruker SSL
- [ ] Storage-keys kun i Key Vault
- [ ] HTTPS-only på begge apps
- [ ] CORS-policy strengt
- [ ] Application Insights ikke logger PII
- [ ] Backup-restore test gjennomført
- [ ] Disaster-recovery runbook skrevet

## Kostnad

- **Initial setup:** 0-12 000 NOK (avhengig av om ekstern hjelp trengs)
- **Løpende drift:** ~950 NOK/mnd = ~11 400 NOK/år
- **Skaler opp ved behov:** dokumentert i spec

## Verifikasjon

Følg "Verifikasjon før go-live"-seksjonen i spec-en. Kritiske sjekkpunkter:

1. `https://uptime.dalanekraft.no` → Microsoft-login
2. Reader-bruker har lese-tilgang men ikke admin
3. KPI-tall for Drivdal feb-2026 matcher lokal versjon (regresjon)
4. Backup-restore til staging-DB fungerer
5. Failed login som ikke-autorisert bruker → 401

## Når ferdig

Skriv `OVERLEVERING-2026-MM-DD-DEPLOY.md` med:
- Prod-URL
- Liste over Azure-ressurser
- Liste over onboardede brukere med roller
- Første ukens uptime
- Eventuelle issues
- Skjermbilde av prod-app

Foreslåtte oppfølginger:
- Microsoft Teams-notifikasjon for ukentlig digest
- Mobile-vennlig UI
- Power BI-dashboard som leser fra prod-DB for ledelsen
