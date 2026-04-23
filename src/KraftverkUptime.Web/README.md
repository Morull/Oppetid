# KraftverkUptime.Web

Blazor WebAssembly frontend. Snakker kun med API via `/api/v1/...`. Settes opp med `ApiBaseAddress` i `wwwroot/appsettings.json`.

## Kjør lokalt

```sh
dotnet run --project src/KraftverkUptime.Web
```

Default port: http://localhost:5180. Sørg for at API kjører på http://localhost:5080 samtidig.

## Autentisering

V1: `AnonymousUserContextProvider` injiseres for å la skjelettet kjøre uten Entra ID.
V2: bytt i `Program.cs` til MSAL + `ApiAuthorizationMessageHandler` uten å endre sider.
