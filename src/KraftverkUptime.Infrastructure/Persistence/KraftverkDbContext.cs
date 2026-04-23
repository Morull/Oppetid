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
                .HasFilter("\"DeletedAt\" IS NULL");
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
                .HasFilter("\"DeletedAt\" IS NULL");

            // Hovedoppslags-index for IUptimePeriodProvider (finn siste import som dekker periode).
            b.HasIndex(x => new { x.PlantId, x.PeriodStartUtc, x.PeriodEndUtc });
            b.HasIndex(x => x.ImportedAtUtc);
        });

        modelBuilder.ApplyOwnedEntityFilters(_queryContext);
    }
}
