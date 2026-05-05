using KraftverkUptime.Core.Security;
using KraftverkUptime.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace KraftverkUptime.Infrastructure.Persistence;

/// <summary>
/// Hovedkontekst. Domenemoduler får egne skjemaer via OnModelCreating-hook i egne moduler
/// (modelBuilder.HasDefaultSchema("settlement") i modul sin registrering).
/// </summary>
public sealed class KraftverkDbContext : DbContext
{
    private readonly IQueryContext _queryContext;

    public KraftverkDbContext(DbContextOptions<KraftverkDbContext> options, IQueryContext queryContext)
        : base(options)
    {
        _queryContext = queryContext;
    }

    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();
    public DbSet<PlantConfigurationEntry> PlantConfigurations => Set<PlantConfigurationEntry>();
    public DbSet<PlantRegistration> Plants => Set<PlantRegistration>();
    public DbSet<SettlementImport> SettlementImports => Set<SettlementImport>();
    public DbSet<DowntimeAnnotationEntry> DowntimeAnnotations => Set<DowntimeAnnotationEntry>();
    public DbSet<DowntimeCategoryEntry> DowntimeCategories => Set<DowntimeCategoryEntry>();
    public DbSet<CauseAliasEntry> CauseAliases => Set<CauseAliasEntry>();
    public DbSet<SignalMapEntry> SignalMaps => Set<SignalMapEntry>();
    public DbSet<SampleFactEntry> SampleFacts => Set<SampleFactEntry>();
    public DbSet<ClassifiedEventEntry> ClassifiedEvents => Set<ClassifiedEventEntry>();
    public DbSet<MarketPriceEntry> MarketPrices => Set<MarketPriceEntry>();
    public DbSet<DamEntry> Dams => Set<DamEntry>();
    public DbSet<DataSourceExpectation> DataSourceExpectations => Set<DataSourceExpectation>();
    public DbSet<DataImport> DataImports => Set<DataImport>();
    public DbSet<DataCompletenessOverride> DataCompletenessOverrides => Set<DataCompletenessOverride>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema("core");

        modelBuilder.Entity<AuditLogEntry>(b =>
        {
            b.ToTable("audit_log");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).UseIdentityAlwaysColumn();
            b.Property(x => x.PayloadJson).HasColumnType("jsonb");
            b.HasIndex(x => x.TimestampUtc);
            b.HasIndex(x => new { x.EntityType, x.EntityId });
            b.HasIndex(x => x.CorrelationId);
        });

        modelBuilder.Entity<PlantConfigurationEntry>(b =>
        {
            b.ToTable("plant_configuration");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).UseIdentityAlwaysColumn();
            b.Property(x => x.ValueJson).HasColumnType("jsonb");
            b.HasIndex(x => new { x.OwnerOrgId, x.PlantId, x.Key }).IsUnique()
                .HasFilter("\"deleted_at\" IS NULL");
        });

        modelBuilder.Entity<PlantRegistration>(b =>
        {
            b.ToTable("plants");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasMaxLength(64);
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.Property(x => x.Type).HasConversion<string>().HasMaxLength(32);
            b.Property(x => x.TimeZone).HasMaxLength(64);
            b.Ignore(x => x.PlantId); // Computed fra Id – ikke mappet til kolonne.
        });

        modelBuilder.Entity<SettlementImport>(b =>
        {
            b.ToTable("settlement_imports");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).UseIdentityAlwaysColumn();
            b.Property(x => x.OwnerOrgId).HasMaxLength(64).IsRequired();
            b.Property(x => x.PlantId).HasMaxLength(64);
            b.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
            b.Property(x => x.BlobPath).HasMaxLength(512).IsRequired();
            b.Property(x => x.PlantName).HasMaxLength(200).IsRequired();
            b.Property(x => x.SchemaVersion).HasMaxLength(32).IsRequired();
            b.Property(x => x.CorrelationId).HasMaxLength(64);

            // Idempotens: samme fil lastet opp to ganger = én rad (upsert).
            // Filter-string matcher eksisterende konvensjon i PlantConfigurationEntry.
            b.HasIndex(x => new { x.OwnerOrgId, x.PlantId, x.IdempotencyKey })
                .IsUnique()
                .HasFilter("\"deleted_at\" IS NULL");

            // Hovedoppslags-index for IUptimePeriodProvider (finn siste import som dekker periode).
            b.HasIndex(x => new { x.PlantId, x.PeriodStartUtc, x.PeriodEndUtc });
            b.HasIndex(x => x.ImportedAtUtc);
        });

        modelBuilder.Entity<DowntimeAnnotationEntry>(b =>
        {
            b.ToTable("downtime_annotations");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).UseIdentityAlwaysColumn();
            b.Property(x => x.OwnerOrgId).HasMaxLength(64).IsRequired();
            b.Property(x => x.PlantId).HasMaxLength(64).IsRequired();
            b.Property(x => x.CategoryId).HasMaxLength(64).IsRequired();
            b.Property(x => x.Comment).HasMaxLength(2000);
            b.Property(x => x.CreatedBy).HasMaxLength(128);
            b.Property(x => x.UpdatedBy).HasMaxLength(128);
            b.Property(x => x.DeletedBy).HasMaxLength(128);

            // Hovedoppslag: hent alle annoteringer for et anlegg som overlapper [from, to).
            b.HasIndex(x => new { x.PlantId, x.StartUtc, x.EndUtc })
                .HasFilter("\"deleted_at\" IS NULL");

            // FK til kategori (uten cascade — vi tillater ikke sletting av system-kategorier
            // og deaktivering av brukerkategorier skal ikke slette annoteringer).
            b.HasOne<DowntimeCategoryEntry>()
                .WithMany()
                .HasForeignKey(x => x.CategoryId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DowntimeCategoryEntry>(b =>
        {
            b.ToTable("downtime_categories");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasMaxLength(64);
            b.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
            b.Property(x => x.ColorHex).HasMaxLength(16).IsRequired();
            b.Property(x => x.UnitStateOverride).HasConversion<string>().HasMaxLength(32).IsRequired();
            b.Property(x => x.Description).HasMaxLength(2000); // nullable
            b.HasIndex(x => x.SortOrder);
        });

        modelBuilder.Entity<CauseAliasEntry>(b =>
        {
            b.ToTable("cause_aliases");
            b.HasKey(x => x.CauseCode);
            b.Property(x => x.CauseCode).HasMaxLength(128);
            b.Property(x => x.OwnerOrgId).HasMaxLength(64).IsRequired();
            b.Property(x => x.DisplayText).HasMaxLength(200).IsRequired();
        });

        // SCADA foundation — ref ANALYSE-NEDETID-SCADA.md + Spec KASKADE-DAMMER
        modelBuilder.Entity<SignalMapEntry>(b =>
        {
            b.ToTable("signal_map");
            b.HasKey(x => new { x.PlantId, x.SignalId });
            b.Property(x => x.PlantId).HasMaxLength(64).IsRequired();
            b.Property(x => x.SignalId).HasMaxLength(128).IsRequired();
            b.Property(x => x.CsvColumn).HasMaxLength(256).IsRequired();
            b.Property(x => x.Unit).HasMaxLength(32).IsRequired();
            b.Property(x => x.Role).HasConversion<string>().HasMaxLength(64).IsRequired();
            b.Property(x => x.OwnerOrgId).HasMaxLength(64).IsRequired();
            b.Property(x => x.DamId).HasMaxLength(64); // nullable
            b.HasIndex(x => x.PlantId);
            b.HasIndex(x => new { x.PlantId, x.Role });
            b.HasIndex(x => new { x.PlantId, x.DamId, x.Role })
                .HasDatabaseName("ix_signal_map_dam");
        });

        modelBuilder.Entity<SampleFactEntry>(b =>
        {
            b.ToTable("sample_facts");
            b.HasKey(x => new { x.AssetId, x.SignalId, x.TimeUtc });
            b.Property(x => x.AssetId).HasMaxLength(64).IsRequired();
            b.Property(x => x.SignalId).HasMaxLength(128).IsRequired();
            b.HasIndex(x => new { x.AssetId, x.TimeUtc });
        });

        modelBuilder.Entity<ClassifiedEventEntry>(b =>
        {
            b.ToTable("classified_events");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).UseIdentityAlwaysColumn();
            b.Property(x => x.OwnerOrgId).HasMaxLength(64).IsRequired();
            b.Property(x => x.PlantId).HasMaxLength(64).IsRequired();
            b.Property(x => x.State).HasConversion<string>().HasMaxLength(32).IsRequired();
            b.Property(x => x.CauseCode).HasMaxLength(64);
            b.Property(x => x.SourcesJson).HasColumnType("jsonb");
            b.Property(x => x.Rationale).HasMaxLength(2000);
            b.HasIndex(x => new { x.PlantId, x.StartUtc });
        });

        modelBuilder.Entity<MarketPriceEntry>(b =>
        {
            b.ToTable("market_prices");
            b.HasKey(x => new { x.PriceArea, x.TimeUtc });
            b.Property(x => x.PriceArea).HasMaxLength(8).IsRequired();
            b.Property(x => x.Source).HasMaxLength(16).IsRequired();
            b.HasIndex(x => x.TimeUtc);
        });

        modelBuilder.Entity<DamEntry>(b =>
        {
            b.ToTable("dams");
            b.HasKey(x => new { x.PlantId, x.DamId });
            b.Property(x => x.PlantId).HasMaxLength(64).IsRequired();
            b.Property(x => x.DamId).HasMaxLength(64).IsRequired();
            b.Property(x => x.Name).HasMaxLength(128).IsRequired();
            b.Property(x => x.OwnerOrgId).HasMaxLength(64).IsRequired();
            b.HasIndex(x => x.PlantId);
            // Hovedoppslag for OverflowQueryService — finn terminal-dam per plant.
            b.HasIndex(x => new { x.PlantId, x.IsTurbineIntake });
        });

        // SPEC-IMPORT-COMPLETENESS: forventede datakilder per anlegg.
        modelBuilder.Entity<DataSourceExpectation>(b =>
        {
            b.ToTable("data_source_expectations");
            b.HasKey(x => new { x.PlantId, x.SourceType });
            b.Property(x => x.PlantId).HasMaxLength(64).IsRequired();
            b.Property(x => x.SourceType).HasMaxLength(32).IsRequired();
            b.Property(x => x.Cadence).HasMaxLength(16).IsRequired();
        });

        // SPEC-IMPORT-COMPLETENESS: logg av faktiske importer.
        modelBuilder.Entity<DataImport>(b =>
        {
            b.ToTable("data_imports");
            b.HasKey(x => x.ImportId);
            b.Property(x => x.PlantId).HasMaxLength(64).IsRequired();
            b.Property(x => x.SourceType).HasMaxLength(32).IsRequired();
            b.Property(x => x.FileName).HasMaxLength(255);
            b.Property(x => x.FileHash).HasMaxLength(64);
            b.Property(x => x.UserId).HasMaxLength(128);
            // Hovedoppslag: hent alle importer for et anlegg + kilde sortert
            // på periode (matrise-build).
            b.HasIndex(x => new { x.PlantId, x.SourceType, x.PeriodFromUtc })
                .HasDatabaseName("ix_imports_plant_source_period");
            b.HasIndex(x => x.ImportedAtUtc);
        });

        // SPEC-IMPORT-COMPLETENESS: manuelle overstyringer av celle-status.
        // Drifts-leder kan markere en celle som komplett etter visuell sjekk
        // i SCADA HMI (eks. "ingen alarmer i april — fredelig drift").
        modelBuilder.Entity<DataCompletenessOverride>(b =>
        {
            b.ToTable("data_completeness_overrides");
            b.HasKey(x => new { x.PlantId, x.SourceType, x.PeriodUtc });
            b.Property(x => x.PlantId).HasMaxLength(64).IsRequired();
            b.Property(x => x.SourceType).HasMaxLength(32).IsRequired();
            b.Property(x => x.Reason).HasMaxLength(500);
            b.Property(x => x.OverriddenByUserId).HasMaxLength(128).IsRequired();
        });

        modelBuilder.ApplyOwnedEntityFilters(_queryContext);
    }
}
