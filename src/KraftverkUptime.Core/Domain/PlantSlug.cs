using System.Text;

namespace KraftverkUptime.Core.Domain;

/// <summary>
/// Konverterer et kanonisk anleggs-navn til en stabil slug for bruk som
/// <c>plant_id</c>. Slugen er ASCII, lowercase og uten skille-tegn.
///
/// Eksempler:
/// <list type="bullet">
///   <item>"Løgjen"     → "logjen"</item>
///   <item>"Grødemfoss" → "grodemfoss"</item>
///   <item>"Øgreyfoss"  → "ogreyfoss"</item>
///   <item>"Stølskraft" → "stolskraft"</item>
///   <item>"Vikeså"     → "vikesa"</item>
/// </list>
///
/// Slugs er kontrakts-stabile — endring av reglene endrer plant_id-er som
/// allerede er persistert. Tester dekker hele bindingsrøret av eksempler.
/// </summary>
public static class PlantSlug
{
    /// <summary>
    /// Konverterer et navn til en slug. Returnerer tom streng for null/blankt
    /// input. Tegn håndteres slik:
    /// <list type="bullet">
    ///   <item>æ/Æ → a, ø/Ø → o, å/Å → a</item>
    ///   <item>Bokstaver/sifre beholdes (lowercase)</item>
    ///   <item>Mellomrom, bindestrek, understrek og alle andre tegn fjernes</item>
    /// </list>
    /// </summary>
    public static string ToSlug(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var trimmed = name.Trim();
        var sb = new StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            switch (ch)
            {
                case 'æ':
                case 'Æ':
                    sb.Append('a');
                    break;
                case 'ø':
                case 'Ø':
                    sb.Append('o');
                    break;
                case 'å':
                case 'Å':
                    sb.Append('a');
                    break;
                default:
                    if (char.IsLetterOrDigit(ch))
                    {
                        sb.Append(char.ToLowerInvariant(ch));
                    }
                    // alt annet (mellomrom, bindestrek, _, tegnsetting) hoppes over
                    break;
            }
        }
        return sb.ToString();
    }
}
