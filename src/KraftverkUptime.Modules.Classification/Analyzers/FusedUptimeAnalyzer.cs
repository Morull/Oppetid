using KraftverkUptime.Core.Analysis;
using KraftverkUptime.Modules.Classification.Dtos;

namespace KraftverkUptime.Modules.Classification.Analyzers;

/// <summary>
/// Stub for Nivå 3+-analyzeren som kombinerer settlement + SCADA + hydrologi
/// + CMMS til én fused klassifisering. Ikke implementert i v1 – krever
/// eieravtale for SCADA-integrasjon.
///
/// <para>Utvidelsesplan (dokumentert her, ingen kode endres i andre moduler):</para>
/// <list type="number">
///   <item>Nivå 1 (hydrologi): Ny modul <c>KraftverkUptime.Modules.Hydrology</c>
///         eksponerer <c>IHydrologicalDataSource</c> og en <c>ResourceClassifier</c>
///         som overstyrer ResourceUnavailable-heuristikken i
///         <c>SettlementUptimeAnalyzer</c> med faktisk vassføringsdata.
///         <c>FusedUptimeAnalyzer.AnalyzeAsync</c> implementerer merge-logikken:
///         ta settlement-klassifiseringen som base, overstyrer den for timer
///         der hydrologi bekrefter/motbeviser RU.</item>
///   <item>Nivå 2 (marked): Ny modul <c>KraftverkUptime.Modules.Market</c>
///         leverer spotprognose og vannverdiestimat; brukes til å re-klassifisere
///         RS-timer med høyere confidence.</item>
///   <item>Nivå 3 (SCADA): Ny modul <c>KraftverkUptime.Modules.Scada.OpcUa</c>
///         eksponerer <c>IScadaDataSource</c> med alarm/trip/aggregat-strømmer.
///         FusedUptimeAnalyzer overstyrer FO/PO-klassifiseringer med faktisk
///         driftstilstand fra SCADA.</item>
///   <item>Nivå 4 (CMMS): Ny modul <c>KraftverkUptime.Modules.Cmms.Ifs</c> eller
///         <c>.Maximo</c> berikes med arbeidsordre-tags som fyller inn CauseCode
///         med faktisk rot-årsak.</item>
/// </list>
///
/// <para>Kontrakten er allerede <c>IAnalyzer&lt;UptimePeriod, UptimeReport&gt;</c>,
/// samme som <see cref="SettlementUptimeAnalyzer"/>. Consumers trenger ikke å
/// vite hvilken analyzer som er registrert – byttes ved én linje i
/// <c>ClassificationModule.RegisterServices</c>.</para>
///
/// <para>Datamodellen støtter allerede dette: <c>ClassifiedPeriod.Sources</c> er
/// <c>IReadOnlyList&lt;string&gt;</c> og kan inneholde flere kilder samtidig
/// (<c>["Settlement","Scada"]</c>); <c>Confidence</c> øker når flere kilder bekrefter
/// samme klassifisering. Ingen Core-endring trengs.</para>
/// </summary>
public sealed class FusedUptimeAnalyzer : IAnalyzer<UptimePeriod, UptimeReport>
{
    public Task<UptimeReport> AnalyzeAsync(UptimePeriod input, CancellationToken ct)
    {
        throw new NotImplementedException(
            "FusedUptimeAnalyzer er en kontrakts-stub for Nivå 3+. " +
            "SCADA-modul må eksistere før denne kan implementeres. " +
            "Inntil da: bruk SettlementUptimeAnalyzer.");
    }
}
