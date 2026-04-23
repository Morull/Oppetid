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
using KraftverkUptime.Infrastructure.Reporting;
using KraftverkUptime.Infrastructure.Security;
using KraftverkUptime.Infrastructure.Storage;
using KraftverkUptime.Modules.Reporting;
using KraftverkUptime.Modules.Settlement.Jobs;
using KraftverkUptime.Modules.Settlement.Persistence;
using Microsoft.EntityFrameworkCore;
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

        // --- Settlement-import persistens + period provider ---
        // Infrastructure eier KraftverkDbContext og er derfor riktig sted for
        // disse implementasjonene. Selve modulene (Settlement, Reporting) tar
        // ikke EF-avhengighet.
        services.AddScoped<ISettlementImportRecorder, DbSettlementImportRecorder>();
        services.AddScoped<IUptimePeriodProvider, SettlementUptimePeriodProvider>();

        // --- Events ---
        services.AddSingleton<IEventPublisher, InProcEventPublisher>();
        services.AddScoped<IEventHandler<SettlementImportedEvent>, ClassifyOnImportedHandler>();

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
