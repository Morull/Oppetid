using Azure.Identity;
using Azure.Storage.Blobs;
using KraftverkUptime.Core.Configuration;
using KraftverkUptime.Core.Events;
using KraftverkUptime.Core.Jobs;
using KraftverkUptime.Core.Modules;
using KraftverkUptime.Core.Notifications;
using KraftverkUptime.Core.Security;
using KraftverkUptime.Core.Storage;
using KraftverkUptime.Infrastructure.Configuration;
using KraftverkUptime.Infrastructure.Events;
using KraftverkUptime.Infrastructure.Jobs;
using KraftverkUptime.Infrastructure.Notifications;
using KraftverkUptime.Infrastructure.Options;
using KraftverkUptime.Infrastructure.Persistence;
using KraftverkUptime.Infrastructure.Persistence.Repositories;
using KraftverkUptime.Infrastructure.Reporting;
using KraftverkUptime.Infrastructure.Security;
using KraftverkUptime.Infrastructure.Storage;
using KraftverkUptime.Modules.Annotations.Repositories;
using KraftverkUptime.Modules.Scada.Repositories;
using KraftverkUptime.Modules.Reporting;
using KraftverkUptime.Modules.Reporting.Portefolje;
using KraftverkUptime.Modules.Reporting.Storage;
using KraftverkUptime.Modules.Settlement.Jobs;
using KraftverkUptime.Modules.Settlement.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace KraftverkUptime.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Registrerer all default-infrastruktur. Kalles fra Api, Worker og enhver test-host.
    /// Oppgradering av én seam = erstatt en Add* i denne metoden. Regel 5 i modul-prinsippene.
    /// </summary>
    public static IServiceCollection AddKraftverkInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // --- Options med validering ---
        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<StorageOptions>()
            .Bind(configuration.GetSection(StorageOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<JobQueueOptions>()
            .Bind(configuration.GetSection(JobQueueOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // --- EF Core ---
        services.AddDbContext<KraftverkDbContext>((sp, options) =>
        {
            var db = sp.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            options.UseNpgsql(db.ConnectionString, npg =>
            {
                npg.CommandTimeout(db.CommandTimeoutSeconds);
                npg.EnableRetryOnFailure();
            });
            options.UseSnakeCaseNamingConvention();

            if (db.EnableSensitiveDataLogging)
            {
                options.EnableSensitiveDataLogging();
            }
        });

        // --- Sikkerhet / brukerkontekst ---
        // Oppgradering til EntraIdUserContext i v2: bytt denne linjen til
        //   services.AddScoped<ICurrentUser, EntraIdUserContext>();
        services.AddScoped<ICurrentUser, SystemUserContext>();
        services.AddScoped<IQueryContext, NoopQueryContext>();
        services.AddScoped<IAuditLogger, DbAuditLogger>();

        // --- Cache ---
        services.AddMemoryCache();
        services.AddDistributedMemoryCache(); // v2: AddStackExchangeRedisCache.

        // --- Konfig per anlegg ---
        services.AddScoped<IPlantConfiguration, DbPlantConfiguration>();
        services.AddScoped<PlantClassificationConfigProvider>();

        // --- Kaskade-modell (dammer per anlegg) ---
        services.AddScoped<KraftverkUptime.Core.Domain.IDamRepository,
                          KraftverkUptime.Infrastructure.Persistence.DbDamRepository>();
        services.AddScoped<KraftverkUptime.Core.Domain.IPlantOverflowConfigProvider,
                          KraftverkUptime.Infrastructure.Persistence.DbPlantOverflowConfigProvider>();

        // --- Settlement-import persistens + period provider ---
        // Infrastructure eier KraftverkDbContext og er derfor riktig sted for
        // disse implementasjonene. Selve modulene (Settlement, Reporting) tar
        // ikke EF-avhengighet.
        services.AddScoped<ISettlementImportRecorder, DbSettlementImportRecorder>();
        services.AddScoped<IUptimePeriodProvider, SettlementUptimePeriodProvider>();

        // --- Annoteringer (manuell nedetidsmerking) ---
        services.AddScoped<IDowntimeAnnotationRepository, EfDowntimeAnnotationRepository>();
        services.AddScoped<IDowntimeCategoryRepository, EfDowntimeCategoryRepository>();

        // --- SCADA foundation (signal_map + sample_facts + classified_events) ---
        services.AddScoped<ISignalMapRepository, EfSignalMapRepository>();
        services.AddScoped<IScadaSampleRepository, EfScadaSampleRepository>();
        // 15-min-pipelinen: separat tabell core.sample_facts_fine — Spec
        // NESTE-CHAT-EFFEKTIVITET-15MIN.md.
        services.AddScoped<IScadaSampleFineRepository, EfScadaSampleFineRepository>();
        services.AddScoped<IClassifiedEventRepository, EfClassifiedEventRepository>();
        services.AddScoped<KraftverkUptime.Modules.Scada.Import.IScadaImportService, KraftverkUptime.Infrastructure.Scada.ScadaImportService>();

        // --- UptimeReport-lagring (blob, JSON) ---
        // BlobUptimeReportStore er den ekte lagringen; CachingUptimeReportStore er en
        // per-prosess caching-dekorator (kort TTL, invalider-ved-skriving) som hindrer
        // at samme rapport-blob leses 2–4× per forespørsel (settlement-KPI, capture-rate,
        // nedetid, datakvalitet) og på tvers av nær-samtidige forespørsler. Blob-I/O er
        // flaskehalsen i rapport-laget. Deler den samme IMemoryCache-singletonen som er
        // registrert i «--- Cache ---»-blokken over (egne «uptimereport|»-nøkler).
        services.AddSingleton<BlobUptimeReportStore>();
        services.AddSingleton<IUptimeReportStore>(sp => new CachingUptimeReportStore(
            sp.GetRequiredService<BlobUptimeReportStore>(),
            sp.GetRequiredService<IMemoryCache>()));

        // --- Portefølje-aggregator (Steg 6) ---
        services.AddScoped<IPortfolioQueryService, KraftverkUptime.Infrastructure.Reporting.PortfolioQueryService>();

        // --- Portefølje Vakt-ROI (dashboard på tvers av alle anlegg) ---
        services.AddScoped<KraftverkUptime.Modules.Reporting.Portefolje.IPortfolioVaktRoiQueryService,
                          KraftverkUptime.Infrastructure.Reporting.PortfolioVaktRoiQueryService>();

        // --- Tilsig-basert "ville-overflow"-estimator (alternativ til SCADA-direkte) ---
        services.AddScoped<KraftverkUptime.Infrastructure.Reporting.InflowOverflowQueryService>();

        // --- Capture rate (Spec CAPTURE-RATE) ---
        services.AddScoped<KraftverkUptime.Modules.Reporting.CaptureRate.ICaptureRateQueryService,
                          KraftverkUptime.Infrastructure.Reporting.CaptureRateQueryService>();

        // Episode-analyse query-service (FORBEDRINGSFORSLAG-EFFEKTIVITET.md Del 3) —
        // henter effektivitets-data + time-spotpriser fra DB og kjører den rene
        // analyzer-en. Eksponeres via /api/v1/effektivitet/{plant}/episoder.
        services.AddScoped<KraftverkUptime.Infrastructure.Reporting.EffektivitetEpisodeQueryService>();

        // Portefølje-blikk på effektivitet (FORBEDRINGSFORSLAG-EFFEKTIVITET.md Del 4.1) —
        // én rad per anlegg med snitt-η, sweet-spot, total tapt verdi.
        services.AddScoped<KraftverkUptime.Infrastructure.Reporting.EffektivitetPortfolioQueryService>();

        // --- KAIA-kostnad per rapportperiode (Spec KAIA-KOSTNAD) ---
        services.AddScoped<KraftverkUptime.Modules.Reporting.KaiaCost.IKaiaCostQueryService,
                          KraftverkUptime.Infrastructure.Reporting.KaiaCostQueryService>();

        // --- Økonomi-rapport (Spec NESTE-CHAT-OKONOMI-FANE-PDF.md) ---
        services.AddScoped<KraftverkUptime.Modules.Reporting.Economy.IEconomyReportQueryService,
                          KraftverkUptime.Infrastructure.Reporting.EconomyReportQueryService>();

        // --- Start/stopp-KPI (Spec NESTE-CHAT-START-STOPP-KPI.md) ---
        services.AddScoped<KraftverkUptime.Modules.Reporting.StartStopp.IStartStoppQueryService,
                          KraftverkUptime.Infrastructure.Reporting.StartStoppQueryService>();

        // --- Produksjons-analyse (Hydrogrid plan vs. faktisk) ---
        services.AddScoped<KraftverkUptime.Modules.Reporting.Produksjon.IProduksjonAnalyseService,
                          KraftverkUptime.Infrastructure.Reporting.ProduksjonAnalyseQueryService>();

        // --- Datakvalitet (SPEC-MVP-HARDENING tiltak C) ---
        services.AddScoped<KraftverkUptime.Modules.Reporting.DataQuality.IDataQualityQueryService,
                          KraftverkUptime.Infrastructure.Reporting.DataQualityQueryService>();

        // --- Data-completeness (SPEC-IMPORT-COMPLETENESS) ---
        services.AddScoped<KraftverkUptime.Core.DataCompleteness.IDataImportLogger,
                          KraftverkUptime.Infrastructure.Reporting.DbDataImportLogger>();
        services.AddScoped<KraftverkUptime.Modules.Reporting.DataCompleteness.IDataCompletenessQueryService,
                          KraftverkUptime.Infrastructure.Reporting.DataCompletenessQueryService>();

        // --- Hot-folder watcher (SPEC-AUTO-IMPORT-FOLDER) ---
        services.Configure<KraftverkUptime.Infrastructure.HotFolder.HotFolderOptions>(
            configuration.GetSection(KraftverkUptime.Infrastructure.HotFolder.HotFolderOptions.SectionName));
        services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<
            KraftverkUptime.Infrastructure.HotFolder.HotFolderOptions>>().Value);
        services.AddSingleton<KraftverkUptime.Infrastructure.HotFolder.HotFolderQueue>();
        services.AddSingleton<KraftverkUptime.Infrastructure.HotFolder.HotFolderDetector>();
        services.AddSingleton<KraftverkUptime.Infrastructure.HotFolder.HotFolderDedupCache>();
        services.AddHostedService<KraftverkUptime.Infrastructure.HotFolder.HotFolderWatcher>();
        // HttpClient for å POSTE settlement-filer mot lokal Api (samme prosess).
        // Base-URL settes i Program.cs via configure-callback der den kjenner kestrel-port.
        services.AddHttpClient("HotFolderUpload", c =>
        {
            // Default localhost:5080 — overstyres via miljøvariabel HotFolder:UploadBaseUrl
            var baseUrl = configuration["HotFolder:UploadBaseUrl"] ?? "http://localhost:5080/";
            c.BaseAddress = new Uri(baseUrl);
            // Store 15-min SCADA-fine-filer (multi-anlegg, titalls MB) sprengte den
            // gamle 5-min-timeouten og havnet i quarantine (TaskCanceledException).
            // Default 30 min, overstyrbar via HotFolder:UploadTimeoutMinutes.
            var timeoutMinutes = int.TryParse(
                configuration["HotFolder:UploadTimeoutMinutes"], out var m) && m > 0 ? m : 30;
            c.Timeout = TimeSpan.FromMinutes(timeoutMinutes);
        });

        // --- Events ---
        services.AddSingleton<IEventPublisher, InProcEventPublisher>();
        services.AddScoped<IEventHandler<SettlementImportedEvent>, ClassifyOnImportedHandler>();
        services.AddScoped<IEventHandler<SettlementImportedEvent>, MarketPriceUpsertHandler>();
        services.AddSingleton(TimeProvider.System);

        // --- Varsling ---
        services.AddSingleton<INotificationService, LogNotificationService>();

        // --- Jobbkø ---
        services.AddSingleton<ChannelsJobQueue>();
        services.AddSingleton<IJobQueue>(sp => sp.GetRequiredService<ChannelsJobQueue>());

        // --- Lagring ---
        services.AddSingleton<IFileStorage>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
            if (opts.Provider.Equals("AzureBlob", StringComparison.OrdinalIgnoreCase))
            {
                var blobClient = CreateBlobServiceClient(opts);
                return new BlobFileStorage(blobClient, Microsoft.Extensions.Options.Options.Create(opts));
            }
            return new LocalFileStorage(Microsoft.Extensions.Options.Options.Create(opts));
        });

        return services;
    }

    /// <summary>
    /// Registrerer alle <see cref="IPlatformModule"/>-implementasjoner som er oppgitt.
    /// </summary>
    public static IServiceCollection AddPlatformModules(this IServiceCollection services, params IPlatformModule[] modules)
    {
        foreach (var module in modules)
        {
            module.RegisterServices(services);
        }
        return services;
    }

    /// <summary>
    /// Registrerer JobLoopHostedService. Kalles fra Worker-prosessen (eller Api hvis du vil dele prosess i dev).
    /// </summary>
    public static IServiceCollection AddKraftverkJobLoop(this IServiceCollection services)
    {
        services.AddHostedService<JobLoopHostedService>();
        return services;
    }

    private static BlobServiceClient CreateBlobServiceClient(StorageOptions options)
    {
        // Connection string (Azurite i dev) vs. endpoint + Managed Identity (prod).
        if (!string.IsNullOrWhiteSpace(options.ConnectionString)
            && (options.ConnectionString.Contains("AccountKey=", StringComparison.OrdinalIgnoreCase)
                || options.ConnectionString.Contains("DefaultEndpointsProtocol=", StringComparison.OrdinalIgnoreCase)))
        {
            return new BlobServiceClient(options.ConnectionString);
        }

        if (!string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return new BlobServiceClient(new Uri(options.ConnectionString), new DefaultAzureCredential());
        }

        throw new InvalidOperationException("Storage.ConnectionString må settes når Provider = AzureBlob.");
    }
}
