using System.ComponentModel.DataAnnotations;

namespace KraftverkUptime.Infrastructure.Options;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>"Local" eller "AzureBlob". Local bruker filsystem, AzureBlob bruker Azurite i dev og Azure Blob i prod.</summary>
    [Required]
    [RegularExpression("^(Local|AzureBlob)$", ErrorMessage = "Storage.Provider må være 'Local' eller 'AzureBlob'.")]
    public string Provider { get; set; } = "Local";

    /// <summary>Rotmappe når Provider = Local.</summary>
    public string LocalRootPath { get; set; } = "./_filestore";

    /// <summary>Connection string eller endpoint URL for Azurite/Azure Blob.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Containernavn i Azure Blob.</summary>
    public string ContainerName { get; set; } = "kraftverkuptime";
}
