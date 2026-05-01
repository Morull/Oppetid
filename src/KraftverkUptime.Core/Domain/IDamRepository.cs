namespace KraftverkUptime.Core.Domain;

/// <summary>
/// Repository for <see cref="Dam"/>-entiteter. Hvert anlegg har én eller
/// flere dammer; nøyaktig én er terminal-dam (<c>IsTurbineIntake = true</c>),
/// håndhevet av en deferrable unique-constraint i databasen.
///
/// Designvalg: separat repository (ikke utvidelse av <c>IPlantConfiguration</c>
/// som er en generisk key/value-cache). Holdes i Core for at modulene skal
/// kunne avhenge av abstraksjonen uten å dra inn EF.
/// </summary>
public interface IDamRepository
{
    /// <summary>Henter alle dammer for et anlegg, sortert etter cascade_position.</summary>
    Task<IReadOnlyList<Dam>> GetForPlantAsync(string plantId, CancellationToken ct);

    /// <summary>
    /// Returnerer dammen som er inntak til turbin (<c>IsTurbineIntake = true</c>).
    /// Returnerer null hvis ingen dam er flagget som terminal — kun et data-
    /// integritets-problem som beskytter mot manuell DB-edit; backfill
    /// garanterer minst én pr anlegg.
    /// </summary>
    Task<Dam?> GetTerminalDamAsync(string plantId, CancellationToken ct);

    /// <summary>Oppretter en ny dam. Constraints validerer terminal-uniqueness.</summary>
    Task AddAsync(Dam dam, CancellationToken ct);

    /// <summary>
    /// Oppdaterer eksisterende dam (for HRV/LRV/Volume eller flytting av
    /// <see cref="Dam.IsTurbineIntake"/>-markøren). Constraint er deferrable
    /// så vi kan flytte intake-markøren mellom dammer i én transaksjon.
    /// </summary>
    Task UpdateAsync(Dam dam, CancellationToken ct);
}
