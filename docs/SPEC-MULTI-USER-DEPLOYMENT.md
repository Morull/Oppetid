# Spec: Multi-user-deployment av KraftverkUptime

**Status:** Klar til planlegging (2026-05-03)
**Estimat:** 3-5 dager initial setup + løpende drift
**Bakgrunn:** I dag kjører appen lokalt på Mortens PC. For å gi tilgang til flere i Dalane Kraft (drifts-leder, kollegaer, ledelse, vakt) må den deployes til delt infrastruktur med autentisering, sikker datalagring, backup og overvåkning.

## Mål

Få appen kjørende på en delt server som:

1. Er tilgjengelig fra alle Dalane Kraft-PC-er via internt domenenavn (eller VPN hvis ekstern bruk)
2. Krever Microsoft-pålogging (Entra ID) — ingen anonymous tilgang
3. Skiller mellom lese-brukere (drifts-leder, ledelse) og admin-brukere (Morten)
4. Har automatisk backup av database og blob-storage
5. Lar Morten oppdatere appen uten at andre brukere blir avbrutt
6. Har delt mappe for auto-import slik at alle kan slippe filer

## To deployment-alternativer

### Alternativ A: Azure cloud (anbefalt)

| Komponent | Tjeneste | Estimert månedskost |
|---|---|---|
| App-host | Azure App Service (Linux, B1) | ~700 NOK |
| Database | Azure Database for PostgreSQL Flexible (B1ms) | ~150 NOK |
| Blob storage | Azure Blob Storage | ~50 NOK |
| Secrets | Azure Key Vault | ~10 NOK |
| Hot folder | Azure File Share + Azure Storage Sync | ~30 NOK |
| Logg/overvåkning | Application Insights (gratis tier) | 0 |
| Backup | Innebygd i Postgres + Blob Storage | inkludert |
| **Sum** | | **~950 NOK/mnd** |

**Fordeler:**
- Microsoft Entra ID allerede tilgjengelig hos Dalane Kraft (M365)
- Innebygde backup- og monitoring-tjenester
- Skalerer hvis bruksmønsteret vokser
- Oppdateringer via GitHub Actions / Azure DevOps — null nedetid for brukere
- Tilgjengelig overalt med VPN eller direkte (HTTPS + Entra ID-auth)

**Ulemper:**
- Løpende månedskostnad
- Krever Azure-konto med kostnadssted/budsjett
- Initial setup tar 1-2 dager

### Alternativ B: On-prem server hos Dalane Kraft

Krever en Windows Server eller Ubuntu-VM/fysisk maskin på Dalane Krafts nettverk.

| Komponent | Hvordan |
|---|---|
| App-host | IIS eller Kestrel + Nginx på Linux-VM |
| Database | PostgreSQL i Docker eller native installasjon |
| Blob storage | Lokal disk eller MinIO (S3-kompatibel) |
| Secrets | Windows Credential Store eller HashiCorp Vault |
| Hot folder | Windows-share på samme server |
| Logg/overvåkning | Seq eller Grafana + Loki, manuelt oppsett |
| Backup | Robocopy/pg_dump til separat server eller skybackup |
| **Sum** | | **0 ekstern kost** (kun strøm + drift-tid) |

**Fordeler:**
- Ingen løpende skykostnad
- Data forblir på Dalane Kraft-nettverket (compliance hvis relevant)
- Full kontroll

**Ulemper:**
- Krever vedlikehold av OS, sikkerhetspatches, backups
- VPN nødvendig for ekstern tilgang
- Begrenset skalerbarhet
- Initial setup tar lenger (manuelt sett opp alt)

**Min anbefaling: Alternativ A (Azure).** Dalane Kraft bruker allerede Microsoft 365 → Entra ID er på plass. ~950 NOK/mnd er ubetydelig kostnad i kraftverk-kontekst, og du sparer titalls timer per år i drift.

## Resten av specen forutsetter Alternativ A

## Forutsetninger fra Dalane Kraft

| Punkt | Hvordan skaffe |
|---|---|
| Azure-abonnement | IT-avdeling eller direkte via M365 admin → Azure portal |
| Kostnadssted godkjent for ~12 000 NOK/år | Avklar med ledelse/regnskap |
| Domenenavn (`uptime.dalanekraft.no` eller lignende) | DNS-administrator legger til CNAME |
| HTTPS-sertifikat | Azure App Service-administrert (gratis Let's Encrypt) |
| Entra ID-administrator-tilgang | Sannsynligvis IT/admin-rolle |
| Liste over brukere som skal ha tilgang | Drifts-leder bekrefter |
| Liste over brukere som skal være admin | Sannsynligvis bare Morten + 1 backup |

## Implementasjons-faser

### Fase 1: Azure-infrastruktur (4-6 timer)

**1.1 Lag Azure-ressurser via Azure CLI eller portal**

```powershell
# Logg inn
az login

# Opprett ressursgruppe
az group create --name kraftverkuptime-prod --location norwayeast

# PostgreSQL Flexible Server (Burstable B1ms — minste passende tier)
az postgres flexible-server create `
    --resource-group kraftverkuptime-prod `
    --name kraftverkuptime-db `
    --location norwayeast `
    --admin-user kraftverkadmin `
    --admin-password <generert-passord> `
    --sku-name Standard_B1ms `
    --tier Burstable `
    --storage-size 32 `
    --version 16 `
    --high-availability Disabled `
    --backup-retention 14

# Tillat Azure-tjenester å koble seg til
az postgres flexible-server firewall-rule create `
    --resource-group kraftverkuptime-prod `
    --name kraftverkuptime-db `
    --rule-name AllowAzure `
    --start-ip-address 0.0.0.0 `
    --end-ip-address 0.0.0.0

# Storage-konto for blob + filshare
az storage account create `
    --name kraftverkuptimest `
    --resource-group kraftverkuptime-prod `
    --location norwayeast `
    --sku Standard_LRS `
    --kind StorageV2

az storage container create --name reports --account-name kraftverkuptimest
az storage container create --name imports --account-name kraftverkuptimest
az storage share create --name hotfolder-imports --account-name kraftverkuptimest

# Key Vault for secrets
az keyvault create `
    --name kraftverkuptime-kv `
    --resource-group kraftverkuptime-prod `
    --location norwayeast `
    --enable-rbac-authorization

# App Service Plan (B1 Linux — minste plan med always-on)
az appservice plan create `
    --name kraftverkuptime-plan `
    --resource-group kraftverkuptime-prod `
    --location norwayeast `
    --sku B1 `
    --is-linux

# Web App for API
az webapp create `
    --name kraftverkuptime-api `
    --resource-group kraftverkuptime-prod `
    --plan kraftverkuptime-plan `
    --runtime "DOTNETCORE:10.0"

# Web App for Web (Blazor)
az webapp create `
    --name kraftverkuptime-web `
    --resource-group kraftverkuptime-prod `
    --plan kraftverkuptime-plan `
    --runtime "DOTNETCORE:10.0"

# Application Insights
az monitor app-insights component create `
    --app kraftverkuptime-insights `
    --location norwayeast `
    --resource-group kraftverkuptime-prod
```

**1.2 Konfigurer secrets i Key Vault**

```powershell
# Database connection string
az keyvault secret set --vault-name kraftverkuptime-kv `
    --name "Database--ConnectionString" `
    --value "Host=kraftverkuptime-db.postgres.database.azure.com;Database=kraftverkuptime;Username=kraftverkadmin;Password=<passord>;SslMode=Require"

# Storage connection string
az keyvault secret set --vault-name kraftverkuptime-kv `
    --name "Storage--ConnectionString" `
    --value "<connection-string-fra-storage-konto>"

# ENTSO-E token (hvis CR-spec inkluderer det)
az keyvault secret set --vault-name kraftverkuptime-kv `
    --name "MarketData--EntsoE--Token" `
    --value "<din-token>"

# Hydrogrid API client credentials (hvis anvendelig)
az keyvault secret set --vault-name kraftverkuptime-kv `
    --name "Hydrogrid--ClientId" `
    --value "<id>"
az keyvault secret set --vault-name kraftverkuptime-kv `
    --name "Hydrogrid--ClientSecret" `
    --value "<secret>"
```

**1.3 Gi App Services tilgang til Key Vault**

```powershell
# Aktiver managed identity på begge web apps
az webapp identity assign --name kraftverkuptime-api --resource-group kraftverkuptime-prod
az webapp identity assign --name kraftverkuptime-web --resource-group kraftverkuptime-prod

# Hent identity-IDene og gi Key Vault Reader-rolle
$apiIdentity = az webapp identity show --name kraftverkuptime-api --resource-group kraftverkuptime-prod --query principalId -o tsv
az role assignment create --assignee $apiIdentity --role "Key Vault Secrets User" --scope <key-vault-resource-id>
```

### Fase 2: Entra ID-konfigurasjon (2-3 timer)

**2.1 Opprett app-registrering i Entra ID**

Via Azure Portal → Microsoft Entra ID → App registrations → New registration:

- Navn: `KraftverkUptime`
- Supported account types: Single tenant (Dalane Kraft)
- Redirect URI: `https://uptime.dalanekraft.no/signin-oidc` (legg til etter at custom domain er på plass)

Notér ned `Application (client) ID` og `Directory (tenant) ID`.

**2.2 Definer roller**

Under Manifest, legg til:

```json
"appRoles": [
    {
        "allowedMemberTypes": ["User"],
        "displayName": "Reader",
        "description": "Lese-tilgang til alle KPI-er og rapporter",
        "value": "Reader",
        "id": "<generer-ny-guid>",
        "isEnabled": true
    },
    {
        "allowedMemberTypes": ["User"],
        "displayName": "Admin",
        "description": "Full tilgang inkludert import, annotering og admin-funksjoner",
        "value": "Admin",
        "id": "<generer-ny-guid>",
        "isEnabled": true
    }
]
```

**2.3 Tilordne brukere/grupper**

Under Enterprise applications → KraftverkUptime → Users and groups → Add:

- Drifts-leder + ledelse + relevante kollegaer → Reader-rolle
- Morten + 1 backup → Admin-rolle

Anbefaling: lag to Entra-grupper (`KraftverkUptime-Reader`, `KraftverkUptime-Admin`) i stedet for direkte bruker-tilordning. Lettere å administrere når noen slutter eller bytter rolle.

**2.4 Konfigurer appen for Entra**

I `appsettings.json` (overstyres per miljø):

```json
{
  "AzureAd": {
    "Instance": "https://login.microsoftonline.com/",
    "TenantId": "<tenant-id>",
    "ClientId": "<client-id>",
    "Domain": "dalanekraft.no",
    "CallbackPath": "/signin-oidc"
  }
}
```

I `Program.cs`, eksisterende auth-oppsett fra MVP-hardening-fase A bør allerede være på plass. Verifiser at det er aktivt.

### Fase 3: Custom domain + HTTPS (1-2 timer)

**3.1 DNS-konfigurasjon**

Hos Dalane Krafts DNS-administrator:

```
uptime.dalanekraft.no    CNAME    kraftverkuptime-web.azurewebsites.net
api.uptime.dalanekraft.no CNAME    kraftverkuptime-api.azurewebsites.net
```

**3.2 Verifiser domain i App Service**

```powershell
az webapp config hostname add --webapp-name kraftverkuptime-web `
    --resource-group kraftverkuptime-prod `
    --hostname uptime.dalanekraft.no

az webapp config hostname add --webapp-name kraftverkuptime-api `
    --resource-group kraftverkuptime-prod `
    --hostname api.uptime.dalanekraft.no
```

**3.3 Aktiver gratis App Service-administrert sertifikat (Let's Encrypt-equivalent)**

Via Azure Portal → App Service → TLS/SSL settings → Private Key Certificates → Create App Service Managed Certificate. Gjør for begge apps. Bind sertifikatet til respektive hostname med SNI.

### Fase 4: CI/CD med GitHub Actions (2-3 timer)

For at oppdateringer skal kunne deployes uten nedetid og uten at Morten manuelt kopierer filer.

**4.1 Lag GitHub Actions workflow**

`.github/workflows/deploy-prod.yml`:

```yaml
name: Deploy to Azure (production)

on:
  push:
    branches: [main]
  workflow_dispatch:

jobs:
  build-and-deploy:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      
      - name: Build and test
        run: |
          dotnet restore
          dotnet build --no-restore
          dotnet test --no-build
      
      - name: Publish API
        run: dotnet publish src/KraftverkUptime.Api -c Release -o ./publish-api
      
      - name: Publish Web
        run: dotnet publish src/KraftverkUptime.Web -c Release -o ./publish-web
      
      - name: Deploy API to Azure
        uses: azure/webapps-deploy@v3
        with:
          app-name: kraftverkuptime-api
          publish-profile: ${{ secrets.AZURE_API_PUBLISH_PROFILE }}
          package: ./publish-api
      
      - name: Deploy Web to Azure
        uses: azure/webapps-deploy@v3
        with:
          app-name: kraftverkuptime-web
          publish-profile: ${{ secrets.AZURE_WEB_PUBLISH_PROFILE }}
          package: ./publish-web
      
      - name: Run database migrations
        run: |
          dotnet tool install --global dotnet-ef
          dotnet ef database update --project src/KraftverkUptime.Infrastructure --startup-project src/KraftverkUptime.Api
        env:
          ConnectionStrings__Database: ${{ secrets.PROD_DATABASE_CONNECTION }}
```

Publish profiles hentes fra Azure Portal → App Service → Get publish profile, lagres som GitHub-secret.

**4.2 Slot-basert deploy for null nedetid (valgfritt, anbefalt for prod)**

```powershell
# Lag staging-slot
az webapp deployment slot create --name kraftverkuptime-api `
    --resource-group kraftverkuptime-prod `
    --slot staging
```

GitHub Actions deployer til staging-slot, smoke-tester, og swapper til production. Brukerne ser aldri nedetid.

### Fase 5: Auto-import hot folder via Azure Files (3-4 timer)

Knytter til `SPEC-AUTO-IMPORT-FOLDER.md`. For multi-user må mappa være delt.

**5.1 Map Azure File Share som Windows-drive på Dalane Kraft-nettverket**

```powershell
# På hver Dalane Kraft-PC som skal kunne droppe filer
$connectTestResult = Test-NetConnection -ComputerName kraftverkuptimest.file.core.windows.net -Port 445
if ($connectTestResult.TcpTestSucceeded) {
    cmd.exe /C "cmdkey /add:`"kraftverkuptimest.file.core.windows.net`" /user:`"localhost\kraftverkuptimest`" /pass:`"<storage-key>`""
    New-PSDrive -Name K -PSProvider FileSystem -Root "\\kraftverkuptimest.file.core.windows.net\hotfolder-imports" -Persist
}
```

Resultat: alle som har konfigurert `K:`-drive kan slippe filer i `K:\inbox\` og appen plukker dem opp innen 30 sekunder.

**5.2 Konfigurer App Service til å mounte samme filshare**

```powershell
az webapp config storage-account add `
    --resource-group kraftverkuptime-prod `
    --name kraftverkuptime-api `
    --custom-id hotfolder `
    --storage-type AzureFiles `
    --account-name kraftverkuptimest `
    --share-name hotfolder-imports `
    --mount-path /mnt/imports
```

Appens `HotFolder:RootPath` settes til `/mnt/imports` i prod-config.

**Alternativ enklere oppsett:** drag-drop via UI fortsetter å fungere. Hot folder kan utsettes hvis det er for komplisert i første runde.

### Fase 6: Backup og disaster recovery (1-2 timer)

**6.1 Database**

Azure Database for PostgreSQL Flexible Server har innebygd backup:

- Default 7-dagers backup, kan utvides til 35 dager
- Point-in-time-restore tilgjengelig

```powershell
az postgres flexible-server update `
    --resource-group kraftverkuptime-prod `
    --name kraftverkuptime-db `
    --backup-retention 14
```

**6.2 Blob storage**

Aktiver soft delete (90 dager) og versioning:

```powershell
az storage blob service-properties delete-policy update `
    --account-name kraftverkuptimest `
    --enable true `
    --days-retained 90

az storage account blob-service-properties update `
    --account-name kraftverkuptimest `
    --resource-group kraftverkuptime-prod `
    --enable-versioning true
```

**6.3 Restore-prosedyre**

Dokumenter i `docs/RUNBOOK-DISASTER-RECOVERY.md`:

1. Hvordan restore Postgres til et tidligere punkt
2. Hvordan rulle tilbake en blob til tidligere versjon
3. Kontaktpersoner og passord-storage
4. RTO/RPO: 4 timer / 1 time

### Fase 7: Overvåkning og varsling (2-3 timer)

**7.1 Application Insights**

Allerede opprettet i fase 1. Koble til appene:

```powershell
$instrumentationKey = az monitor app-insights component show `
    --app kraftverkuptime-insights `
    --resource-group kraftverkuptime-prod `
    --query instrumentationKey -o tsv

az webapp config appsettings set `
    --resource-group kraftverkuptime-prod `
    --name kraftverkuptime-api `
    --settings APPLICATIONINSIGHTS_CONNECTION_STRING="<connection-string>"
```

**7.2 Alerts**

Sett opp Azure Monitor-alerts som sender e-post til Morten ved:

- App Service nede > 5 min
- Postgres CPU > 80 % vedvarende
- Failed requests > 5 % de siste 15 min
- Storage-konto > 80 % full
- Database backup feilet

```powershell
# Eksempel: alert på app down
az monitor metrics alert create `
    --name "API down" `
    --resource-group kraftverkuptime-prod `
    --scopes <api-resource-id> `
    --condition "avg HttpResponseTime > 30000" `
    --window-size 5m `
    --evaluation-frequency 1m `
    --action <action-group-id>
```

### Fase 8: Brukeronboarding (1-2 timer)

**8.1 Lag enkel onboardings-side**

`docs/USER-GUIDE.md`:

```markdown
# Komme i gang med KraftverkUptime

## Logge inn

1. Åpne https://uptime.dalanekraft.no
2. Klikk "Logg inn med Microsoft"
3. Bruk din Dalane Kraft-konto

Hvis du ikke får tilgang: kontakt Morten for tilgangstildeling.

## Hva kan du se?

- Portefølje (alle anlegg, KPI-oversikt)
- Per anlegg: Nedetid, Vakt-ROI, Capture rate, Effektivitet, Produksjon
- Datakvalitet og import-status

## Spørsmål

Kontakt Morten direkte eller IT-helpdesk.
```

**8.2 Send velkomst-mail til alle brukere**

Når Entra-roller er på plass, send e-post med:
- Lenke til appen
- Kort overblikk
- Liste over hva de kan se

## Sikkerhets-sjekkliste før go-live

- [ ] Alle endepunkter krever auth (verifisert i MVP-hardening)
- [ ] Admin-endepunkter krever Admin-rolle
- [ ] Audit-logg fungerer for alle write-operasjoner
- [ ] Postgres bruker SSL (kreves av Azure Flexible)
- [ ] Storage-keys ikke i kode — kun i Key Vault
- [ ] HTTPS-only på begge apps (`az webapp update --https-only true`)
- [ ] CORS-policy strengt — kun web-apps eget domene
- [ ] Application Insights ikke logger PII (sjekk `ApplicationInsightsTelemetryProcessor`)
- [ ] Backup-restore test gjennomført minst én gang før prod
- [ ] Disaster-recovery runbook dokumentert

## Vedlikehold etter go-live

| Oppgave | Frekvens | Tidsbruk |
|---|---|---|
| Sjekk Application Insights for feil | Ukentlig | 10 min |
| Sjekk import-completeness-dashboard | Ukentlig | 5 min |
| Pakke-oppdateringer (security patches) | Månedlig | 30 min |
| Database backup-verifikasjon | Kvartalsvis | 15 min |
| Disaster-recovery-test (restore til staging) | Halvårlig | 2 timer |
| Bruker-tilgangsvurdering | Halvårlig | 30 min |
| Storage-rensing av gamle imports | Årlig | 1 time |

## Skaleringsbeslutninger senere

Hvis bruken vokser, oppgrader trinnvis:

| Symptom | Handling |
|---|---|
| App Service CPU > 70 % vedvarende | Skift fra B1 til B2 (~1 400 NOK/mnd) |
| Postgres CPU > 80 % vedvarende | Skift fra B1ms til B2s (~400 NOK/mnd) |
| Storage > 80 % | Aktiver lifecycle policy for å arkivere gamle imports |
| > 50 samtidige brukere | Vurder Premium-tier App Service for auto-scaling |

## Akseptansekriterier

### Funksjonelle

1. `https://uptime.dalanekraft.no` returnerer Microsoft-login
2. Etter login: Reader-bruker ser KPI-sider men IKKE admin-funksjoner
3. Admin-bruker ser alt, kan importere, annotere
4. Drag-drop av settlement-fil fungerer
5. KPI-tall for Drivdal feb-2026 matcher lokal versjon (regresjons-sjekk)

### Drift

6. App Service har > 99.5 % uptime månedlig (Azure SLA er 99.95 %)
7. Backup gjøres daglig automatisk
8. Application Insights mottar telemetri
9. Alerts sender e-post når terskel overskrides
10. Deploy via GitHub Actions tar < 10 min ende-til-ende

### Sikkerhet

11. Anonymous tilgang returnerer 401 på alle non-public endepunkter
12. Postgres ikke direkte eksponert mot internett (kun App Service-VNET)
13. Storage-konto har soft delete + versioning
14. HTTPS-only og TLS 1.2+
15. Audit-logg har minst én rad per write-operasjon, ingen PII i logg

## Kostnads-estimat første år

| Post | Beløp |
|---|---|
| Azure-tjenester (~950 NOK/mnd × 12) | ~11 400 NOK |
| Initial setup (1 dag konsulent hvis ekstern) | 0-12 000 NOK |
| Sertifikat | 0 (App Service-administrert) |
| **Sum første år** | **11 400-23 400 NOK** |

År 2+: kun løpende ~11 400 NOK + eventuell skalering.

## Implementasjons-rekkefølge

| Fase | Innhold | Estimat |
|---|---|---|
| 0 | Avklar Azure-konto, kostnadssted, domenenavn med IT/ledelse | 1-2 dager kalender |
| 1 | Azure-infrastruktur (Postgres, App Service, Storage, Key Vault) | 4-6 t |
| 2 | Entra ID app-registrering + roller + brukertildeling | 2-3 t |
| 3 | Custom domain + HTTPS | 1-2 t |
| 4 | GitHub Actions CI/CD | 2-3 t |
| 5 | Hot folder via Azure File Share | 3-4 t (kan utsettes) |
| 6 | Backup-konfig og restore-test | 1-2 t |
| 7 | Application Insights + alerts | 2-3 t |
| 8 | Brukerguide + onboardings-mail | 1-2 t |

**Estimat totalt:** 3-5 dager arbeid spredt over 1-2 uker (avhengig av tilgang til Azure og DNS).

## Antakelser

1. **Dalane Kraft har Microsoft 365** og dermed Entra ID tilgjengelig
2. **IT-administrator kan gi Azure-abonnement-tilgang** og opprette DNS-records
3. **Nettverksadgang fra Dalane Kraft til Azure norwayeast** uten spesielle restriksjoner
4. **MVP-hardening-spec er implementert** (Entra ID-skjelett ferdig, alle endepunkter krever auth)
5. **Storage-volumet vokser ikke vesentlig** — 11 anlegg × 12 måneder × ~500 KB per import er ubetydelig

## Ut-av-scope for v1

- Multi-region failover (Norway East er nok for én operatør)
- Active Directory federering (Entra ID alene er nok når alle brukere er der)
- API-tilgang for tredjeparter
- Mobil-app
- SLA-overvåkning av eksterne dataleverandører (KAIA, Hydrogrid)
- Penetrasjons-testing (kan komme senere)

## Verifikasjon før go-live

```powershell
# 1. URL-tilgjengelighet
curl https://uptime.dalanekraft.no
# Forventet: 200 OK eller redirect til login

# 2. Auth fungerer
# Manuelt: åpne i browser, logg inn med din Dalane-konto

# 3. KPI-data identiske mot lokal versjon
# Manuelt: kjør samme periode for Drivdal lokalt og i Azure, sammenlign

# 4. Backup-restore-test
az postgres flexible-server restore `
    --name kraftverkuptime-db-restored `
    --resource-group kraftverkuptime-prod `
    --restore-time "2026-05-03T08:00:00Z" `
    --source-server kraftverkuptime-db
# Verifiser at restored DB har data

# 5. Failed login som ikke-autorisert bruker
# Manuelt: logg inn med konto som ikke er i KraftverkUptime-roller — skal nektes

# 6. Audit-logg
psql <conn> -c "SELECT * FROM core.audit_log ORDER BY timestamp_utc DESC LIMIT 10;"
# Forventet: rader for innloggings-aktivitet og write-operasjoner
```

## Når ferdig

Skriv `OVERLEVERING-2026-MM-DD-DEPLOY.md` med:

- URL til prod (`https://uptime.dalanekraft.no`)
- Liste over Azure-ressurser opprettet
- Liste over brukere onboardet (med roller)
- Backup-konfigurasjon bekreftet
- Første ukens uptime-statistikk
- Eventuelle issues som måtte løses underveis

Foreslåtte oppfølginger etter deploy:
- Sett opp Microsoft Teams-notifikasjon når data-completeness-digest sendes
- Mobile-vennlig UI-forbedring (hvis brukerne åpner fra telefon)
- Power BI-dashboard som leser fra samme database for ledelsesrapporter
- Multi-tenant hvis Dalane Kraft skal selge platformen til andre kraftselskaper
