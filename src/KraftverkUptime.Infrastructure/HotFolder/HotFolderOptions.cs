using System.Collections.ObjectModel;

namespace KraftverkUptime.Infrastructure.HotFolder;

/// <summary>
/// Konfigurasjon for hot-folder-overvåkningen (SPEC-AUTO-IMPORT-FOLDER).
/// Bind via <c>builder.Configuration.GetSection("HotFolder")</c>.
/// </summary>
public sealed class HotFolderOptions
{
    public const string SectionName = "HotFolder";

    /// <summary>Slå av watcher hvis miljøet ikke skal scanne disk (CI / multi-instance).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Rot-mappa som overvåkes. Filer detekteres her.</summary>
    public string RootPath { get; set; } = @"C:\Morten\00 Oppetid\CSV Eksporter";

    /// <summary>Undermappe der vellykkede importer flyttes.</summary>
    public string DoneFolderName { get; set; } = "done";

    /// <summary>Undermappe der feilede importer havner med .error.txt-vedlegg.</summary>
    public string QuarantineFolderName { get; set; } = "quarantine";

    /// <summary>
    /// Hvor ofte mappa skannes. Bruker polling i stedet for FileSystemWatcher
    /// fordi sistnevnte kan miste hendelser ved nettverksshare. Kost på
    /// lokal disk: ~1-10 ms per scan = forsvinnende lite.
    /// Sett til 0 (eller bruk <see cref="ManualOnly"/>) for å skru av auto-polling.
    /// </summary>
    public int PollIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Skru av automatisk polling. Watcher-en kjører fortsatt og er klar
    /// til å scanne, men den scanner kun når brukeren trigger via
    /// <c>POST /api/v1/hot-folder/scan-now</c> (Skann nå-knappen i UI).
    /// </summary>
    public bool ManualOnly { get; set; }

    /// <summary>
    /// Filnavn-mønstre (case-insensitive substring) som skal ignoreres av
    /// watcher-en. Brukes for å skille test-fixtures, README-er, osv. fra
    /// reelle eksport-filer som skal auto-importeres.
    /// </summary>
    public Collection<string> ExcludePatterns { get; } = new()
    {
        "README",
        ".lock",
        "~$",                    // Excel/Word lock-files
        ".hotfolder-",           // Dedup-cache + framtidige interne state-filer
    };

    /// <summary>
    /// Hvor lenge en fil må være "stabil" (uendret last-write-time) før
    /// vi prosesserer den. Hindrer at vi leser en fil mens den blir kopiert.
    /// </summary>
    public int FileStabilitySeconds { get; set; } = 5;

    /// <summary>
    /// Hvor mange dager innholds-hashen til en prosessert fil holdes i
    /// dedup-cachen. Filer med samme hash (eks. dropped two ganger) hopper
    /// over import-pipelinen og rapporteres som DUPLIKAT.
    /// </summary>
    public int DedupRetentionDays { get; set; } = 14;

    /// <summary>
    /// Filnavn for JSON-cachen som persistere dedup-records mellom restarts.
    /// Plasseres i <see cref="RootPath"/>. Filen er utelatt fra import via
    /// <see cref="ExcludePatterns"/> (matcher prefiks <c>.</c>).
    /// </summary>
    public string DedupCacheFileName { get; set; } = ".hotfolder-dedup.json";

    /// <summary>
    /// Undermappe der duplikat-filer flyttes (separat fra <see cref="DoneFolderName"/>
    /// for å være tydelig på at innholdet allerede er importert tidligere).
    /// </summary>
    public string DuplicatesFolderName { get; set; } = "duplicates";

    /// <summary>
    /// Filnavn-prefikser som ruter til anlegg når content-sniff ikke
    /// kan finne entydig prefiks. Brukes også av SCADA-tag-prefiks-detect.
    /// </summary>
    public Dictionary<string, string> PlantPrefixMap { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        // SCADA-tag-konvensjon: korte prefikser (ikke fullt anleggs-navn)
        ["DRIVDAL"] = "drivdal",
        ["DRIV"] = "drivdal",
        ["LINDLAND"] = "lindland",
        ["LIND"] = "lindland",
        ["HAUKLAND"] = "haukland",
        ["HAUK"] = "haukland",
        ["HONNE"] = "honnefoss",
        ["LIAVT"] = "honnefoss",     // LIAVT-tags i Honnefoss-eksport tilhører Honnefoss-inntak
        ["GRODEM"] = "grodemfoss",
        ["GRODEMFOSS"] = "grodemfoss",
        ["OGREY"] = "ogreyfoss",
        ["OGREYFOSS"] = "ogreyfoss",
        ["OGREY1"] = "ogreyfoss",      // G1-generator-side (ny eksport fra 2026-06)
        ["OGREY2"] = "ogreyfoss",      // G2-generator-side (ny eksport fra 2026-06)
        ["LOGJEN"] = "logjen",
        ["LOG"] = "logjen",
        ["ORSDAL"] = "orsdalen",
        ["ORSDALEN"] = "orsdalen",
        ["LIAVATN"] = "liavatn",     // dam-relatert SCADA — ikke kraftverk
        ["VIKESA"] = "vikesa",
        ["VIKE"] = "vikesa",
        ["STOLS"] = "stolskraft",
        ["STOLSKRAFT"] = "stolskraft",
    };
}
