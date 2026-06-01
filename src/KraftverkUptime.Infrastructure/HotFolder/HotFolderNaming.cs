using System.Text.RegularExpressions;

namespace KraftverkUptime.Infrastructure.HotFolder;

/// <summary>
/// Hjelpere for å bygge mål-filnavn ved flytting til done/duplicates/quarantine
/// uten at navnet vokser ubegrenset.
///
/// Bakgrunn (path-too-long-fiks): tidligere prependet
/// <c>MoveToDoneAsync</c>/<c>MoveToDuplicatesAsync</c> et nytt
/// <c>{plant}_{source}_{timestamp}_</c>-prefiks foran det eksisterende
/// <c>file.Name</c>. Når en fil gikk gjennom pipelinen flere ganger (re-import,
/// restore, sync) akkumulerte prefiksene seg til navnet oversteg Windows
/// MAX_PATH (260) og <c>File.Move</c> kastet «path too long».
///
/// Løsning: strip alltid alle akkumulerte stamp-prefikser ned til det rene
/// eksport-navnet før vi stamper på nytt, og kapp total lengde trygt.
/// </summary>
public static class HotFolderNaming
{
    /// <summary>
    /// Tidsstempel-token som <c>BuildStampedName</c>-genererte navn bruker:
    /// <c>yyyyMMddTHHmmssfff</c> = 8 sifre + 'T' + 9 sifre. De rene eksport-navnene
    /// fra SCADA/settlement bruker bindestrek og kortere tallgrupper
    /// (f.eks. <c>export-117-tags-avg-hour-20260503-064149</c>,
    /// <c>operlog-export-2026-05-04T05-48-28-763Z</c>) og matcher derfor aldri.
    /// </summary>
    private static readonly Regex StampToken = new(@"\d{8}T\d{9}", RegexOptions.Compiled);

    /// <summary>
    /// Maks lengde på selve filnavnet (uten mappe). Konservativt under MAX_PATH
    /// slik at <c>done/yyyy-MM/</c>-stien + evt. <c>.error.txt</c>/<c>.diag.json</c>-
    /// suffiks får god margin.
    /// </summary>
    public const int MaxFileNameLength = 120;

    /// <summary>
    /// Fjern alle akkumulerte stamp-prefikser slik at vi alltid stamper på det
    /// rene, opprinnelige eksport-navnet. Kutter alt til og med siste
    /// tidsstempel-token. Returnerer originalen uendret hvis ingen stamp finnes
    /// (eller hvis kutting ville gitt tomt navn).
    /// </summary>
    public static string StripStampPrefixes(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return fileName;
        }

        var matches = StampToken.Matches(fileName);
        if (matches.Count == 0)
        {
            return fileName;
        }

        var last = matches[^1];
        var rest = fileName[(last.Index + last.Length)..].TrimStart('_');
        return rest.Length == 0 ? fileName : rest;
    }

    /// <summary>
    /// Bygg et stamp-et filnavn som garantert holder seg under
    /// <see cref="MaxFileNameLength"/>. Stripper først akkumulerte prefikser,
    /// beholder ekstensjonen, og trunkerer kun den rene basen ved behov.
    /// </summary>
    /// <param name="prefix">Stamp som skal settes foran, inkl. avsluttende «_».</param>
    /// <param name="originalName">Filnavnet som flyttes (kan inneholde gamle stamps).</param>
    public static string BuildStampedName(string prefix, string originalName)
    {
        var clean = StripStampPrefixes(originalName);
        var ext = Path.GetExtension(clean);
        var stem = Path.GetFileNameWithoutExtension(clean);

        // Plass igjen til stem etter prefiks + ekstensjon.
        var budget = MaxFileNameLength - prefix.Length - ext.Length;
        if (budget < 8)
        {
            budget = 8; // behold alltid noe gjenkjennelig av navnet
        }

        if (stem.Length > budget)
        {
            // Behold starten — den er mest informativ (anlegg/kilde/eksport-id).
            stem = stem[..budget];
        }

        return prefix + stem + ext;
    }
}
