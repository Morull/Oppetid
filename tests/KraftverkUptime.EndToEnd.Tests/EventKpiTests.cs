using FluentAssertions;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Modules.Classification.Config;
using KraftverkUptime.Modules.Classification.Dtos;
using KraftverkUptime.Modules.Classification.Kpi;
using KraftverkUptime.Modules.Settlement.Dtos;
using Xunit;

namespace KraftverkUptime.EndToEnd.Tests;

/// <summary>
/// Tester for event-baserte KPI-er (Steg 4 i veikartet):
///   – ForcedOutageEvents: antall sammenhengende FO-blokker
///   – MTBF: SH / events
///   – MTTR: FOH / events
///   – FOR:  FOH / (FOH + SH)
///   – EAF:  (AH − POH − MOH − EFDH) / period_hours
/// </summary>
public class EventKpiTests
{
    private static readonly PlantClassificationConfig Plant = new()
    {
        PlantId = "test", PlantType = PlantType.Regulated, NominalPowerMw = 2.2,
    };

    private static ClassifiedHourlyRow Row(int hourFromStart, UnitState state) =>
        new()
        {
            Row = new SettlementHourlyRow
            {
                TimeUtc = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero).AddHours(hourFromStart),
                TimeLocal = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero).AddHours(hourFromStart),
            },
            State = state,
            CauseCode = "test",
            Confidence = 1.0,
            Rationale = "test",
        };

    private static UptimeReport Compute(IEnumerable<ClassifiedHourlyRow> rows) =>
        new UptimeKpiCalculator().Compute(rows.ToList(), Plant);

    private static double GetKpiValue(UptimeReport report, string name) =>
        report.Kpis.Single(k => k.Name == name).Value!.Value;

    private static double? GetKpiNullable(UptimeReport report, string name) =>
        report.Kpis.Single(k => k.Name == name).Value;

    [Fact]
    public void EnEneFOTime_GirEnEvent()
    {
        var rows = new[]
        {
            Row(0, UnitState.InService),
            Row(1, UnitState.ForcedOutage),
            Row(2, UnitState.InService),
        };
        var r = Compute(rows);
        GetKpiValue(r, "ForcedOutageEvents").Should().Be(1);
        GetKpiValue(r, "MTBF_hours").Should().Be(2);  // SH=2, events=1
        GetKpiValue(r, "MTTR_hours").Should().Be(1);  // FOH=1, events=1
    }

    [Fact]
    public void TreSammenhengendeFOTimer_GirEnEvent()
    {
        var rows = new[]
        {
            Row(0, UnitState.InService),
            Row(1, UnitState.ForcedOutage),
            Row(2, UnitState.ForcedOutage),
            Row(3, UnitState.ForcedOutage),
            Row(4, UnitState.InService),
        };
        var r = Compute(rows);
        GetKpiValue(r, "ForcedOutageEvents").Should().Be(1);
        GetKpiValue(r, "MTTR_hours").Should().Be(3);  // FOH=3 / events=1
    }

    [Fact]
    public void ToAdskilteFOEpisoder_GirToEvents()
    {
        var rows = new[]
        {
            Row(0, UnitState.InService),
            Row(1, UnitState.ForcedOutage),  // event 1
            Row(2, UnitState.InService),
            Row(3, UnitState.ForcedOutage),  // event 2
            Row(4, UnitState.ForcedOutage),
            Row(5, UnitState.InService),
        };
        var r = Compute(rows);
        GetKpiValue(r, "ForcedOutageEvents").Should().Be(2);
        GetKpiValue(r, "MTTR_hours").Should().BeApproximately(1.5, 0.001); // FOH=3 / events=2
    }

    [Fact]
    public void TimeHopp_AvslutterEvent()
    {
        // Hull i timene mellom T0 og T2 — FO-radene er ikke sammenhengende.
        var rows = new[]
        {
            Row(0, UnitState.ForcedOutage),
            // Hopp: ingen rad for time 1
            Row(2, UnitState.ForcedOutage),
        };
        var r = Compute(rows);
        GetKpiValue(r, "ForcedOutageEvents").Should().Be(2);
    }

    [Fact]
    public void IngenFO_GirNullEvents_OgUndefinedMTBFMTTR()
    {
        var rows = new[]
        {
            Row(0, UnitState.InService),
            Row(1, UnitState.InService),
        };
        var r = Compute(rows);
        GetKpiValue(r, "ForcedOutageEvents").Should().Be(0);
        GetKpiNullable(r, "MTBF_hours").Should().BeNull();
        GetKpiNullable(r, "MTTR_hours").Should().BeNull();
    }

    [Fact]
    public void FOR_BeregnesSomFOH_DividertMedFOHPlussSH()
    {
        var rows = new[]
        {
            Row(0, UnitState.InService),
            Row(1, UnitState.InService),
            Row(2, UnitState.InService),
            Row(3, UnitState.ForcedOutage),
            Row(4, UnitState.ReserveShutdown),  // skal ikke telle i FOR
        };
        var r = Compute(rows);
        // FOR = 1 / (1 + 3) = 0.25
        GetKpiValue(r, "ForcedOutageRate_FOR").Should().BeApproximately(0.25, 1e-9);
    }

    [Fact]
    public void EAF_TrekkerFraForcedPlanlagtVedlikeholdOgDerating_IkkeVannmangel()
    {
        // 10 timer total (FAGVURDERING #5 — RU teller IKKE lenger som utilgjengelig):
        //   3 InService (SH=3)
        //   2 ForcedOutage (FOH=2)
        //   1 PlannedOutage (POH=1)
        //   1 MaintenanceOutage (MOH=1)
        //   1 ResourceUnavailable (RU=1) — teller som TILGJENGELIG (ikke trukket fra)
        //   1 ForcedDerating (EFDH = 0.5 × 1 = 0.5)
        //   1 PlannedDerating (EFDH += 0.5)
        // EAF = (10 − 0 datahull − 2 FOH − 1 POH − 1 MOH − 1 EFDH) / 10 = 5/10 = 0.5
        var rows = new[]
        {
            Row(0, UnitState.InService),
            Row(1, UnitState.InService),
            Row(2, UnitState.InService),
            Row(3, UnitState.ForcedOutage),
            Row(4, UnitState.ForcedOutage),
            Row(5, UnitState.PlannedOutage),
            Row(6, UnitState.MaintenanceOutage),
            Row(7, UnitState.ResourceUnavailable),
            Row(8, UnitState.ForcedDerating),
            Row(9, UnitState.PlannedDerating),
        };
        var r = Compute(rows);
        GetKpiValue(r, "EquivalentAvailabilityFactor_EAF").Should().BeApproximately(0.5, 1e-9);

        // IEEE-AF på samme datasett (FAGVURDERING #2): reservestopp/vannmangel/
        // derating teller som tilgjengelig; kun FOH+POH+MOH er utilgjengelig.
        // (10 − 0 datahull − 2 FOH − 1 POH − 1 MOH) / 10 = 6/10 = 0,6.
        GetKpiValue(r, "AvailabilityFactorIeee_AF").Should().BeApproximately(0.6, 1e-9);
        // Gammel «leveringsgrad» (SH/(SH+FOH)) er uendret: 3/(3+2) = 0,6 her tilfeldigvis.
        GetKpiValue(r, "AvailabilityFactor_AF").Should().BeApproximately(3.0 / 5.0, 1e-9);
    }

    [Fact]
    public void IeeeAf_EkskludererDatahull_OgTellerReserveSomTilgjengelig()
    {
        // 8 timer: 3 InService, 1 ForcedOutage, 2 ReserveShutdown, 2 datahull (IU).
        // IEEE-AF = (8 − 2 IU − 1 FOH) / (8 − 2 IU) = 5/6 ≈ 0,8333.
        // (Reservestopp teller som tilgjengelig; datahull ekskluderes.)
        var rows = new[]
        {
            Row(0, UnitState.InService),
            Row(1, UnitState.InService),
            Row(2, UnitState.InService),
            Row(3, UnitState.ForcedOutage),
            Row(4, UnitState.ReserveShutdown),
            Row(5, UnitState.ReserveShutdown),
            Row(6, UnitState.InformationUnavailable),
            Row(7, UnitState.InformationUnavailable),
        };
        var r = Compute(rows);
        GetKpiValue(r, "AvailabilityFactorIeee_AF").Should().BeApproximately(5.0 / 6.0, 1e-9);
    }

    [Fact]
    public void CountStateEvents_HelperFungererForVilkaarligState()
    {
        var rows = new[]
        {
            Row(0, UnitState.PlannedOutage),
            Row(1, UnitState.PlannedOutage),
            Row(2, UnitState.InService),
            Row(3, UnitState.PlannedOutage),
        };
        UptimeKpiCalculator.CountStateEvents(rows, UnitState.PlannedOutage).Should().Be(2);
        UptimeKpiCalculator.CountStateEvents(rows, UnitState.InService).Should().Be(1);
    }
}
