using KraftverkUptime.Core.Security;
using KraftverkUptime.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace KraftverkUptime.Infrastructure.Persistence;

/// <summary>
/// Brukes kun av EF Core CLI (dotnet ef migrations add ...). Ikke referert i runtime.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<KraftverkDbContext>
{
    public KraftverkDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("KRAFTVERK_DESIGN_CONNSTR")
            ?? "Host=localhost;Port=5432;Database=kraftverk;Username=kraftverk;Password=kraftverk";

        var builder = new DbContextOptionsBuilder<KraftverkDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention();

        IQueryContext queryContext = new NoopQueryContext();
        return new KraftverkDbContext(builder.Options, queryContext);
    }
}
