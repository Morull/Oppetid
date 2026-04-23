# KraftverkUptime.Worker

Bakgrunnsprosess. Konsumerer `IJobQueue` via `JobLoopHostedService` og dispatcher til `IJobHandler<TJob>`-implementasjoner registrert i DI.

## Kjør lokalt

```sh
dotnet run --project src/KraftverkUptime.Worker
```

## Legge til en ny jobb-handler

1. Definer jobb-record i relevant modul: `public sealed record MyJob(string PlantId, Guid RunId);`
2. Implementer `IJobHandler<MyJob>` i samme modul.
3. Registrer i modulens `RegisterServices`: `services.AddScoped<IJobHandler<MyJob>, MyJobHandler>();`
4. API/planlegger kan nå kalle `IJobQueue.EnqueueAsync(new MyJob(...))`.
