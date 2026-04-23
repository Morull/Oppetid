namespace KraftverkUptime.Core.Domain;

/// <summary>
/// Soft-delete-kontrakt. Global query filter ekskluderer rader med
/// <see cref="DeletedAt"/> satt. Retensjonspolicy per datakategori styrer
/// når slettede rader purgeres fysisk.
/// </summary>
public interface ISoftDeletable
{
    DateTimeOffset? DeletedAt { get; }
    string? DeletedBy { get; }
}
