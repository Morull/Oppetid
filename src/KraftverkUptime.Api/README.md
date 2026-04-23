# KraftverkUptime.Api

Minimal API som eksponerer `/api/v1/...` og `/health/*`. Composition root for API-prosessen.

## Kjør lokalt

```sh
dotnet run --project src/KraftverkUptime.Api
```

Swagger: http://localhost:5080/swagger
Helse:   http://localhost:5080/health/live og /health/ready

## Legge til et endepunkt

1. Lag ny statisk klasse i `Endpoints/`, f.eks. `ReportsEndpoints.cs`.
2. Eksponer `MapReportsV1(this IEndpointRouteBuilder)`-extension.
3. Registrer policy via `.RequireAuthorization(AuthorizationPolicies.PlantAnalyst)`.
4. Kall `app.MapReportsV1()` i `Program.cs`.
