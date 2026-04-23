namespace KraftverkUptime.Core.Storage;

/// <summary>
/// Objektlagring med streng-basert nøkkelrom. Stier bør være
/// hierarkiske med "/"-skilletegn, f.eks. "ownerOrg/plant/type/filename".
///
/// V1: lokalt filsystem i dev, Azure Blob Storage i Azure.
/// Samme grensesnitt begge steder – ingen konditional kode i moduler.
/// </summary>
public interface IFileStorage
{
    /// <summary>Skriver innholdet og returnerer kanonisk sti / URL.</summary>
    Task<string> PutAsync(string path, Stream content, CancellationToken ct = default);

    /// <summary>Returnerer en read-seekable strøm. Caller eier strømmen.</summary>
    Task<Stream> GetAsync(string path, CancellationToken ct = default);

    /// <summary>Sjekker eksistens uten å laste innholdet.</summary>
    Task<bool> ExistsAsync(string path, CancellationToken ct = default);

    /// <summary>Sletter objektet. Idempotent – ingen feil hvis objektet ikke finnes.</summary>
    Task DeleteAsync(string path, CancellationToken ct = default);

    /// <summary>Lister stier med gitt prefiks. Asynkron for å støtte paginert blob-listing.</summary>
    IAsyncEnumerable<string> ListAsync(string prefix, CancellationToken ct = default);
}
