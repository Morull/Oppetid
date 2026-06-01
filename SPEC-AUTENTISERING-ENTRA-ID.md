# SPEC: Microsoft Entra ID-autentisering for KraftverkUptime

**Målgruppe:** Claude Code, kjørt i repo-rota `C:\Morten\00 Oppetid`.
**Skrevet:** 2026-05-29.
**Oppdragsgiver:** Morten (Dalane Kraft bruker M365).

## 0. Sammendrag / mål

Legg innlogging på appen via **Microsoft Entra ID** (Azure AD), uten å endre
endepunktenes policy-tagger. Modus i denne leveransen: **innlogget = full
tilgang** (alle autentiserte brukere får alle roller / `HasAllPlantsAccess`).
Rolle-policyene er allerede på plass og skal kunne strammes inn senere uten å
røre endepunktene.

Hele endringen skal være **flagg-styrt** (`AzureAd:Enabled`). Default = `false`
gir nøyaktig dagens oppførsel (anonym, `SystemUserContext`). Settes flagget
`true` (etter at Azure-oppsett og config-verdier er på plass), aktiveres
innlogging. Dette gjør at den kjørende løsningen ikke brytes før alt er klart.

**Ikke-mål:** rollestyring per bruker, fler-org, Funnel/offentlig eksponering.

## 1. Kontekst — slik kjører appen nå

- .NET 10-løsning. Kjøres som Docker Compose-stack:
  `docker compose -f docker-compose.yml -f docker-compose.tailscale.yml up --build -d`
- **Caddy** (`caddy/Caddyfile`, `docker-compose.tailscale.yml`) er eneste inngang
  på `127.0.0.1:8080`: `/` → web (nginx/Blazor WASM), `/api`,`/swagger`,`/health` → api.
- Alt eksponeres kun via **Tailscale Serve**:
  `https://desktop-r4rfc53.tail68e013.ts.net/` (tailnet only).
- Frontend er **Blazor WebAssembly (standalone)**. `wwwroot/appsettings.json` har
  ingen `ApiBaseAddress` lenger → `Program.cs` faller tilbake til
  `HostEnvironment.BaseAddress`, dvs. **samme origin**. API-klientene kaller
  relative stier (`api/v1/...`), Caddy proxyer `/api` til API-containeren.
- Postgres/azurite/api/web er bundet til `127.0.0.1` i `docker-compose.yml`.

**Konsekvens for auth:** Siden frontend og API deler origin, er redirect-URI og
audience enkle. Det trengs ingen CORS-spesialhåndtering.

## 2. Eksisterende sømmer (allerede i koden — IKKE bygg på nytt)

| Sted | Fil | Status nå |
|---|---|---|
| API authn (placeholder) | `src/KraftverkUptime.Api/Program.cs` | `AddAuthentication().AddJwtBearer("Bearer", o => {...})` uten Authority/Audience |
| API authz | `src/KraftverkUptime.Api/Authorization/AuthorizationExtensions.cs` | Alle policyer + fallback = `RequireAssertion(_ => true)` (tillat alle) |
| Policy-navn | `src/KraftverkUptime.Core/Security/AuthorizationPolicies.cs` | PlantReader/Analyst/Admin, OrgAdmin, SystemAdmin + `All` |
| Brukerkontekst (kontrakt) | `src/KraftverkUptime.Core/Security/ICurrentUser.cs` | UserId, OrgId, Roles, HasAllPlantsAccess, AccessiblePlantIds |
| Brukerkontekst (impl) | `src/KraftverkUptime.Infrastructure/Security/SystemUserContext.cs` | Full tilgang. Registrert i `InfrastructureServiceCollectionExtensions.cs` linje ~80 |
| Frontend brukerkontekst | `src/KraftverkUptime.Web/Services/IUserContextProvider.cs` | `AnonymousUserContextProvider` stub |
| NuGet API | `src/KraftverkUptime.Api/KraftverkUptime.Api.csproj` | `Microsoft.AspNetCore.Authentication.JwtBearer` 10.0.4 ✅ |
| NuGet Web | `src/KraftverkUptime.Web/KraftverkUptime.Web.csproj` | `Microsoft.Authentication.WebAssembly.Msal` 10.0.4 + `...Components.WebAssembly.Authentication` 10.0.4 ✅ |

Begge auth-pakkene er altså allerede referert. Endepunktene er allerede tagget
med `RequireAuthorization(AuthorizationPolicies.X)`; helse-endepunktene i
`Endpoints/HealthEndpoints.cs` har eksplisitt `AllowAnonymous()` (må forbli slik).

## 3. KRITISK fallgruve — hot-folder auto-import

`InfrastructureServiceCollectionExtensions.cs` registrerer en
`HotFolderWatcher` (HostedService) som **poster filer til API-et selv** via en
navngitt HttpClient `"HotFolderUpload"` (base-URL fra `HotFolder:UploadBaseUrl`,
i compose satt til `http://localhost:8080/`). Dette er en maskin-til-maskin-kall
**uten Entra-token**. Når auth skrus på vil disse POST-ene få **401**, og
auto-import slutter å virke stille.

**Løsning (påkrevd i denne leveransen):** En egen, intern autentiseringsscheme
basert på en delt nøkkel-header. Se §4.3 og §4.4.

## 4. Implementasjon — API (`KraftverkUptime.Api`)

### 4.1 Config-modell
Les ny seksjon `AzureAd` + `Internal` fra config. Foreslått options-klasse
`src/KraftverkUptime.Api/Options/AuthOptions.cs`:

```csharp
public sealed class AzureAdOptions
{
    public const string SectionName = "AzureAd";
    public bool Enabled { get; set; }
    public string Instance { get; set; } = "https://login.microsoftonline.com/";
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";   // = API app (client) id
}
```
Intern nøkkel leses som `Internal:ApiKey` (string, kan være tom når auth av).

### 4.2 Program.cs — authn/authz
Erstatt dagens placeholder-blokk (`AddAuthentication().AddJwtBearer("Bearer", ...)`
+ `AddKraftverkAuthorization()`) med flagg-styrt oppsett:

```csharp
var azureAd = builder.Configuration.GetSection(AzureAdOptions.SectionName).Get<AzureAdOptions>() ?? new();
var internalApiKey = builder.Configuration["Internal:ApiKey"];

if (azureAd.Enabled)
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(o =>
        {
            o.Authority = $"{azureAd.Instance.TrimEnd('/')}/{azureAd.TenantId}/v2.0";
            o.Audience = azureAd.ClientId;
            o.TokenValidationParameters.ValidAudiences =
                new[] { azureAd.ClientId, $"api://{azureAd.ClientId}" };
            o.RequireHttpsMetadata = true;
        })
        .AddScheme<InternalApiKeyOptions, InternalApiKeyHandler>(
            InternalApiKeyDefaults.Scheme, o => o.ApiKey = internalApiKey ?? "");

    builder.Services.AddHttpContextAccessor();
    // Overstyrer SystemUserContext fra Infrastructure (siste registrering vinner):
    builder.Services.AddScoped<ICurrentUser, EntraIdUserContext>();
}
else
{
    builder.Services.AddAuthentication().AddJwtBearer("Bearer", o =>
        o.RequireHttpsMetadata = !builder.Environment.IsDevelopment());
}

builder.Services.AddKraftverkAuthorization(azureAd.Enabled);
```

Merk: `AddKraftverkInfrastructure(...)` kalles før dette og registrerer
`ICurrentUser → SystemUserContext`. Den nye linjen i `if`-grenen overstyrer den
kun i API-prosessen. **Worker røres ikke** og beholder `SystemUserContext`
(riktig — worker autentiserer aldri en menneskelig bruker).

### 4.3 AuthorizationExtensions — parameter for flagg
Endre signatur til `AddKraftverkAuthorization(this IServiceCollection services, bool authEnabled)`.

- `authEnabled == false`: behold dagens oppførsel (alle policyer + fallback =
  `RequireAssertion(_ => true)`).
- `authEnabled == true`: hver policy og fallback skal kreve **autentisert bruker**
  via **begge** schemes (JWT + Internal), slik at både innloggede brukere og den
  interne hot-folder-klienten passerer:

```csharp
var schemes = new[] { JwtBearerDefaults.AuthenticationScheme, InternalApiKeyDefaults.Scheme };
AuthorizationPolicy Authed() => new AuthorizationPolicyBuilder(schemes)
    .RequireAuthenticatedUser()
    .Build();
// fallback = Authed(); hver navngitt policy = Authed()  (full tilgang = bare innlogget)
```
Health beholder `AllowAnonymous()` og er upåvirket.

### 4.4 Nye filer i API

**`Security/EntraIdUserContext.cs`** — `ICurrentUser` fra HttpContext-claims.
Full-tilgang-modus:
- `UserId` = `oid`-claim (`http://schemas.microsoft.com/identity/claims/objectidentifier`)
  ev. fallback `sub`/`NameIdentifier`; ved intern-scheme: `"system"`.
- `OrgId` = `tid`-claim ev. `"dalane"` som default.
- `Roles` = `AuthorizationPolicies.All` (alle roller i denne leveransen).
- `HasAllPlantsAccess` = `true`.
- `AccessiblePlantIds` = tom.

**`Security/InternalApiKeyDefaults.cs`**: `public const string Scheme = "Internal";`

**`Security/InternalApiKeyHandler.cs`** + `InternalApiKeyOptions : AuthenticationSchemeOptions`
(`public string ApiKey { get; set; } = "";`). Logikk i `HandleAuthenticateAsync`:
- Hvis `ApiKey` er tom → `AuthenticateResult.NoResult()` (la JWT håndtere).
- Les header `X-Internal-Api-Key`. Match (konstant-tid-sammenligning) mot `ApiKey`:
  - Treff → bygg `ClaimsPrincipal` med `Name="system"` + en rolle-claim per
    `AuthorizationPolicies.All`, scheme = `Internal`. `AuthenticateResult.Success`.
  - Ellers → `NoResult()` (ikke `Fail`, så JWT fortsatt kan validere vanlige brukere).

### 4.5 Hot-folder-klienten sender intern nøkkel
I `InfrastructureServiceCollectionExtensions.cs`, i `AddHttpClient("HotFolderUpload", ...)`:
legg til default-header når nøkkel finnes:

```csharp
var key = configuration["Internal:ApiKey"];
if (!string.IsNullOrWhiteSpace(key))
    c.DefaultRequestHeaders.Add("X-Internal-Api-Key", key);
```
(Harmløst når auth er av; da krever uansett ingen endepunkter auth.)

## 5. Implementasjon — Web (`KraftverkUptime.Web`)

### 5.1 `wwwroot/index.html`
Legg til MSAL-scriptet rett **før** `blazor.webassembly.js`:
```html
<script src="_content/Microsoft.Authentication.WebAssembly.Msal/AuthenticationService.js"></script>
```

### 5.2 `wwwroot/appsettings.json`
Behold "ingen ApiBaseAddress" (samme origin). Legg til (verdier er **ikke**
hemmelige — public client + PKCE):
```json
{
  "AzureAd": {
    "Enabled": false,
    "Authority": "https://login.microsoftonline.com/<TENANT_ID>",
    "ClientId": "<CLIENT_ID>",
    "ValidateAuthority": true
  },
  "ApiScope": "api://<CLIENT_ID>/access_as_user"
}
```

### 5.3 `Program.cs` — flagg-styrt MSAL
`apiBase`-blokken beholdes. Erstatt den enkle `HttpClient`-registreringen med:

```csharp
var authEnabled = builder.Configuration.GetValue<bool>("AzureAd:Enabled");

if (authEnabled)
{
    builder.Services.AddMsalAuthentication(options =>
    {
        builder.Configuration.Bind("AzureAd", options.ProviderOptions.Authentication);
        options.ProviderOptions.LoginMode = "redirect";
        var scope = builder.Configuration["ApiScope"]!;
        options.ProviderOptions.DefaultAccessTokenScopes.Add(scope);
    });

    builder.Services.AddScoped<BaseAddressAuthorizationMessageHandler>();
    builder.Services.AddHttpClient("KraftverkAPI", c => c.BaseAddress = new Uri(apiBase))
        .AddHttpMessageHandler<BaseAddressAuthorizationMessageHandler>();
    builder.Services.AddScoped(sp =>
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("KraftverkAPI"));

    builder.Services.AddScoped<IUserContextProvider, MsalUserContextProvider>();
}
else
{
    builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(apiBase) });
    builder.Services.AddScoped<IUserContextProvider, AnonymousUserContextProvider>();
    // Slik at AuthorizeRouteView i App.razor virker uten MSAL-provider:
    builder.Services.AddScoped<AuthenticationStateProvider, AlwaysAuthenticatedStateProvider>();
    builder.Services.AddAuthorizationCore();
}
```
Behold de eksisterende `AddScoped<ReportsApi>()` osv. — de får riktig
`HttpClient` fra registreringen over.

### 5.4 Nye Web-filer
- **`Services/MsalUserContextProvider.cs`**: leser claims fra
  `AuthenticationStateProvider` (navn/oid/roller). I full-tilgang-modus kan
  `Roles` returnere `["PlantReader","PlantAnalyst","PlantAdmin","OrgAdmin","SystemAdmin"]`.
- **`Services/AlwaysAuthenticatedStateProvider.cs`**: `AuthenticationStateProvider`
  som returnerer en fast autentisert "anon"-principal (kun brukt når auth av).
- **`Pages/Authentication.razor`** (route `/authentication/{action}`):
  ```razor
  @page "/authentication/{action}"
  @using Microsoft.AspNetCore.Components.WebAssembly.Authentication
  <RemoteAuthenticatorView Action="@Action" />
  @code { [Parameter] public string? Action { get; set; } }
  ```
- **`Shared/RedirectToLogin.razor`**: navigerer til `authentication/login` med
  returnUrl (standard MSAL-mønster).

### 5.5 `_Imports.razor`
Legg til:
```razor
@using Microsoft.AspNetCore.Components.Authorization
@using Microsoft.AspNetCore.Components.WebAssembly.Authentication
```

### 5.6 `App.razor`
Pakk `Router` i `CascadingAuthenticationState` og bytt `RouteView` →
`AuthorizeRouteView` med `RedirectToLogin` i `NotAuthorized`. Behold
eksisterende `CascadingValue Value="this"`, tema-providere og `MudThemeProvider`.
Skisse:
```razor
<CascadingAuthenticationState>
  <CascadingValue Value="this">
    <Router AppAssembly="@typeof(Program).Assembly">
      <Found Context="routeData">
        <AuthorizeRouteView RouteData="@routeData" DefaultLayout="@typeof(Layout.MainLayout)">
          <NotAuthorized><RedirectToLogin /></NotAuthorized>
          <Authorizing>...laster...</Authorizing>
        </AuthorizeRouteView>
        <FocusOnNavigate RouteData="@routeData" Selector="h1" />
      </Found>
      <NotFound>...som før...</NotFound>
    </Router>
  </CascadingValue>
</CascadingAuthenticationState>
```

### 5.7 LoginDisplay i `Layout/MainLayout.razor`
Legg en liten `AuthorizeView` i toppbaren: vis `@context.User.Identity?.Name` +
en "Logg ut"-knapp (`Navigation.NavigateToLogout("authentication/logout")`).
`<Authorized>`/`<NotAuthorized>`. Når auth er av, vil `AlwaysAuthenticated...`
gjøre at `<Authorized>` vises med navnet "anon" — det er greit.

## 6. Azure-portal (PREREQ — Morten gjør dette i Entra admin center)

Claude Code kan ikke gjøre dette. Steg:

1. **Entra admin center → App registrations → New registration**
   - Navn: `KraftverkUptime`
   - Supported account types: **Single tenant** (kun denne organisasjonen)
   - Redirect URI: velg **Single-page application (SPA)**, verdi:
     `https://desktop-r4rfc53.tail68e013.ts.net/authentication/login-callback`
   - Register.
2. Noter **Application (client) ID** og **Directory (tenant) ID**.
3. **Authentication** → under SPA-plattformen, legg til flere redirect-URIer:
   - `https://desktop-r4rfc53.tail68e013.ts.net/authentication/logout-callback`
   - `http://localhost:8080/authentication/login-callback` (for lokal test)
   - `http://localhost:8080/authentication/logout-callback`
   - (Ikke huk av implicit grant — MSAL.js bruker auth code + PKCE.)
4. **Expose an API** → sett Application ID URI = `api://<CLIENT_ID>` (default) →
   **Add a scope**: navn `access_as_user`, "Admins and users", display-tekst
   "Tilgang til KraftverkUptime".
5. **API permissions** → Add a permission → My APIs → KraftverkUptime →
   delegated `access_as_user` → **Grant admin consent** (så brukerne slipper
   samtykke-dialog).

## 7. Config-verdier som fylles inn (etter Azure-oppsett)

| Verdi | Hvor |
|---|---|
| `AzureAd__Enabled=true` | API: `docker-compose.yml` (api + worker `environment`) |
| `AzureAd__TenantId=<TENANT_ID>` | API: samme |
| `AzureAd__ClientId=<CLIENT_ID>` | API: samme |
| `Internal__ApiKey=<generert>` | API: samme (api + worker). Generer f.eks. `openssl rand -base64 32`. Legg helst i `.env` og referer som variabel. |
| `AzureAd:Enabled=true`, `Authority`, `ClientId`, `ApiScope` | Web: `src/KraftverkUptime.Web/wwwroot/appsettings.json` (bakes ved build) |

Bruk gjerne `.env` for `Internal__ApiKey` (legg til en variabel i compose), så den
ikke havner i git. `AzureAd`-verdiene er ikke hemmelige.

## 8. Bygg og verifiser

```powershell
cd "C:\Morten\00 Oppetid"
docker compose -f docker-compose.yml -f docker-compose.tailscale.yml up --build -d api worker web caddy
```
1. Åpne `https://desktop-r4rfc53.tail68e013.ts.net/` → skal redirecte til
   Microsoft-innlogging → etter innlogging tilbake til appen.
2. Nettleserens Network-fane: `/api/v1/...`-kall har `Authorization: Bearer ...`
   og svarer 200.
3. Helse fortsatt grønn: `https://.../health/ready`.
4. **Auto-import:** legg en CSV i `CSV Eksporter`-mappa → skal importeres
   (intern nøkkel-header virker). Sjekk `docker compose logs api`.
5. `dotnet test KraftverkUptime.sln` skal fortsatt være grønn. Test-hosten
   (`WebApplicationFactory`) kjører med `AzureAd:Enabled=false` → uendret.

## 9. Rollback

Sett `AzureAd:Enabled=false` (API i compose + Web appsettings) og rebuild, eller
`git checkout -- <filer>`. Den interne nøkkel-headeren er da uvirksom.

## 10. Akseptansekriterier

- [ ] Med `AzureAd:Enabled=false` er oppførselen identisk med i dag (anonym).
- [ ] Med `=true` kreves Microsoft-innlogging for å se appen.
- [ ] Alle `/api/v1`-kall fra frontend bærer gyldig token og returnerer 200.
- [ ] Hot-folder auto-import virker fortsatt (intern scheme).
- [ ] Helse-endepunkter er anonyme og grønne.
- [ ] Ingen hemmeligheter i koden; `Internal:ApiKey` kun i `.env`/miljø.
- [ ] Endepunktenes `RequireAuthorization(...)`-tagger er uendret.
- [ ] Worker bruker fortsatt `SystemUserContext`.
- [ ] `dotnet test` grønn.

## 11. Senere (ikke nå)

- Ekte rollestyring: map Entra-app-roller/grupper → `AuthorizationPolicies`,
  og la `EntraIdUserContext` lese reelle roller + `AccessiblePlantIds`. Bytt
  policy-byggerne i `AuthorizationExtensions` fra `RequireAuthenticatedUser` til
  `RequireRole(...)`. Ingen endring i endepunktene.
- App-roller defineres i Entra (App registration → App roles) og tildeles
  brukere/grupper i Enterprise application.
