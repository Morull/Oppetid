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
    /// fordi sistnevnte kan miste hendelser ved nettverksshare.
    /// </summary>
    public int PollIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Hvor lenge en fil må være "stabil" (uendret last-write-time) før
    /// vi prosesserer den. Hindrer at vi leser en fil mens den blir kopiert.
    /// </summary>
    public int FileStabilitySeconds { get; set; } = 5;

    /// <summary>
    /// Filnavn-prefikser som ruter til anlegg når content-sniff ikke
    /// kan finne entydig prefiks. Brukes også av SCADA-tag-prefiks-detect.
    /// </summary>
    public Dictionary<string, string> PlantPrefixMap { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DRIVDAL"] = "drivdal",
        ["LINDLAND"] = "lindland",
        ["HAUKLAND"] = "haukland",
        ["HONNE"] = "honnefoss",
        ["LIAVT"] = "honnefoss",
        ["GRODEMFOSS"] = "grodemfoss",
        ["OGREYFOSS"] = "ogreyfoss",
        ["LOGJEN"] = "logjen",
        ["ORSDALEN"] = "orsdalen",
        ["LIAVATN"] = "liavatn",
        ["VIKESA"] = "vikesa",
        ["STOLSKRAFT"] = "stolskraft",
    };
}
