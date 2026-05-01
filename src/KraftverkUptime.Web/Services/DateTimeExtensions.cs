namespace KraftverkUptime.Web.Services;

/// <summary>
/// Extension-metoder for trygg konstruksjon av <see cref="DateTimeOffset"/>
/// fra <see cref="DateTime"/>-verdier som kan ha varierende
/// <see cref="DateTime.Kind"/>.
///
/// MudDatePicker returnerer typisk Kind=Unspecified, men i noen flyter
/// (typisk når data har vært gjennom JSON-serialisering) er Kind=Local.
/// <c>new DateTimeOffset(localDateTime, TimeSpan.Zero)</c> kaster
/// <see cref="ArgumentException"/> med meldingen "Argument_OffsetLocalMismatch"
/// fordi Local krever at offset matcher systemets tidssone.
///
/// Disse hjelpere normaliserer Kind til Unspecified slik at vi alltid
/// kan konstruere en DateTimeOffset med Zero-offset uten exception.
/// Antakelse: dato-velger-input representerer datoer som UTC-startverdier
/// for spørringer mot APIet.
/// </summary>
public static class DateTimeExtensions
{
    /// <summary>
    /// Trygg konvertering: tar en (potensielt Kind=Local) DateTime og
    /// returnerer DateTimeOffset med Zero-offset. Dato-delen bevares;
    /// time-delen droppes.
    /// </summary>
    public static DateTimeOffset ToUtcOffsetSafe(this DateTime value)
    {
        var unspecified = DateTime.SpecifyKind(value.Date, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, TimeSpan.Zero);
    }

    /// <summary>Samme som <see cref="ToUtcOffsetSafe(DateTime)"/> for nullable DateTime.</summary>
    public static DateTimeOffset ToUtcOffsetSafe(this DateTime? value)
    {
        if (!value.HasValue) throw new ArgumentNullException(nameof(value));
        return value.Value.ToUtcOffsetSafe();
    }
}
