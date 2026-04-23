namespace KraftverkUptime.Core.Security;

/// <summary>
/// Revisjonslogg for verdiskapende handlinger (skriving, sletting, konfigurasjonsendring,
/// rolleendring). Justering (e) fra Prompt 1 v1: CancellationToken er med.
///
/// Implementasjoner skal være fail-loud: en skrivefeil til audit må bryte transaksjonen
/// slik at handlingen rulles tilbake. Silent-failure i audit er verre enn ingen audit.
/// </summary>
public interface IAuditLogger
{
    Task LogAsync(
        string action,
        string entityType,
        string entityId,
        object? payload,
        CancellationToken ct = default);
}
