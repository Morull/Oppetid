using System.ComponentModel.DataAnnotations;

namespace KraftverkUptime.Infrastructure.Options;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    [Required]
    public string ConnectionString { get; set; } = string.Empty;

    [Range(1, 600)]
    public int CommandTimeoutSeconds { get; set; } = 30;

    public bool EnableSensitiveDataLogging { get; set; } = false;

    public bool RunMigrationsOnStartup { get; set; } = true;
}
