using FluentAssertions;
using KraftverkUptime.Core.Time;
using Xunit;

namespace KraftverkUptime.Core.Tests.Time;

/// <summary>
/// Verifiserer vakt-vindu-logikken (15-07 hverdager + helg/helligdag) og
/// counterfactual "neste arbeidsdag 08:00".
///
/// Alle DateTimeOffset-verdier konstrueres som lokal Oslo-tid og konverteres
/// til UTC før de gis til modellen — slik replikerer vi hvordan UI vil sende
/// inn tidspunkter (lokale klokkeslett observert av drifts-leder).
/// </summary>
public class VaktTidsmodellTests
{
    private static readonly TimeZoneInfo Oslo = TimeZones.Norway;

    private static DateTimeOffset OsloLokal(int year, int month, int day, int hour, int minute = 0)
    {
        var local = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        var offset = Oslo.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    [Fact]
    public void Hverdag_Klokken_Ti_Er_IKKE_Innenfor_Vakt()
    {
        // Onsdag 4. februar 2026, kl 10:00 lokal — ordinær arbeidstid
        var modell = new VaktTidsmodell();
        modell.ErInnenforVakt(OsloLokal(2026, 2, 4, 10)).Should().BeFalse();
    }

    [Fact]
    public void Hverdag_Klokken_Sytten_Er_Innenfor_Vakt()
    {
        // Onsdag 4. februar 2026, kl 17:00 — etter 15:00 cutoff
        var modell = new VaktTidsmodell();
        modell.ErInnenforVakt(OsloLokal(2026, 2, 4, 17)).Should().BeTrue();
    }

    [Fact]
    public void Hverdag_Tidlig_Morgen_Er_Innenfor_Vakt()
    {
        // Onsdag 4. februar 2026, kl 06:00 — før 07:00 cutoff
        var modell = new VaktTidsmodell();
        modell.ErInnenforVakt(OsloLokal(2026, 2, 4, 6)).Should().BeTrue();
    }

    [Fact]
    public void Hverdag_Klokken_Syv_Er_IKKE_Innenfor_Vakt()
    {
        // Akkurat på 07:00 — drifts-personell ankommer, vakt slutter
        var modell = new VaktTidsmodell();
        modell.ErInnenforVakt(OsloLokal(2026, 2, 4, 7)).Should().BeFalse();
    }

    [Fact]
    public void Hele_Lordag_Er_Innenfor_Vakt()
    {
        // Lørdag 7. februar 2026
        var modell = new VaktTidsmodell();
        modell.ErInnenforVakt(OsloLokal(2026, 2, 7, 12)).Should().BeTrue();
        modell.ErInnenforVakt(OsloLokal(2026, 2, 7, 23)).Should().BeTrue();
    }

    [Fact]
    public void Hele_Sondag_Er_Innenfor_Vakt()
    {
        // Søndag 8. februar 2026
        var modell = new VaktTidsmodell();
        modell.ErInnenforVakt(OsloLokal(2026, 2, 8, 9)).Should().BeTrue();
    }

    [Fact]
    public void Helligdag_17_Mai_Er_Innenfor_Vakt_Hele_Dognet()
    {
        // 17. mai 2026 (søndag — uansett innenfor)
        // 17. mai 2027 er mandag — viktig case
        var modell = new VaktTidsmodell();
        modell.ErInnenforVakt(OsloLokal(2027, 5, 17, 12)).Should().BeTrue();
        modell.ErInnenforVakt(OsloLokal(2027, 5, 17, 9)).Should().BeTrue();  // Selv 09:00 på helligdag
    }

    [Fact]
    public void NesteArbeidsdagOppstart_Onsdag_1500_Returnerer_Torsdag_0800()
    {
        // Trip onsdag 4. feb 2026 kl 15:00 → vakt fikser, men counterfactual = torsdag 08:00
        var modell = new VaktTidsmodell();
        var trip = OsloLokal(2026, 2, 4, 15);

        var counterfactual = modell.NesteArbeidsdagOppstart(trip);
        var lokal = TimeZoneInfo.ConvertTime(counterfactual, Oslo);

        lokal.Year.Should().Be(2026);
        lokal.Month.Should().Be(2);
        lokal.Day.Should().Be(5);  // Torsdag
        lokal.Hour.Should().Be(8);
        lokal.Minute.Should().Be(0);
    }

    [Fact]
    public void NesteArbeidsdagOppstart_Fredag_2200_Hopper_Til_Mandag_0800()
    {
        // Fredag 6. feb 2026 kl 22:00 — neste arbeidsdag = mandag 9. feb 08:00
        var modell = new VaktTidsmodell();
        var trip = OsloLokal(2026, 2, 6, 22);

        var counterfactual = modell.NesteArbeidsdagOppstart(trip);
        var lokal = TimeZoneInfo.ConvertTime(counterfactual, Oslo);

        lokal.Day.Should().Be(9);  // Mandag
        lokal.Hour.Should().Be(8);
    }

    [Fact]
    public void NesteArbeidsdagOppstart_Lordag_Fra_Helligdag_Hopper_Forbi()
    {
        // Skjærtorsdag 2. april 2026 kl 14:00 — counterfactual = onsdag 8. april 08:00
        // (skjær 2., lang 3., påske 5./6. (søn/man), og 7. tirsdag = arbeidsdag)
        var modell = new VaktTidsmodell();
        var trip = OsloLokal(2026, 4, 2, 14);

        var counterfactual = modell.NesteArbeidsdagOppstart(trip);
        var lokal = TimeZoneInfo.ConvertTime(counterfactual, Oslo);

        // Tirsdag 7. april er første arbeidsdag (skjær/lang/påske/2.påske er røde, mandag 6. = 2.påskedag)
        lokal.Day.Should().Be(7);
        lokal.Hour.Should().Be(8);
    }

    [Fact]
    public void NesteArbeidsdagOppstart_Mandag_Tidlig_Returnerer_Samme_Dag_0800()
    {
        // Mandag kl 03:00 (vakt-vindu): drifts-personell møter samme dag 08:00
        var modell = new VaktTidsmodell();
        var trip = OsloLokal(2026, 2, 9, 3);

        var counterfactual = modell.NesteArbeidsdagOppstart(trip);
        var lokal = TimeZoneInfo.ConvertTime(counterfactual, Oslo);

        lokal.Day.Should().Be(9);
        lokal.Hour.Should().Be(8);
    }

    [Fact]
    public void Default_Options_Matcher_Drifts_Leder_Spesifikasjon()
    {
        var opts = VaktTidsmodellOptions.Default;
        opts.EttermiddagStart.Should().Be(new TimeSpan(15, 0, 0));
        opts.MorgenCutoff.Should().Be(new TimeSpan(7, 0, 0));
        opts.OppmoteTidspunkt.Should().Be(new TimeSpan(8, 0, 0));
        opts.VaktResponstid.Should().Be(TimeSpan.FromHours(1.0));
    }

    // ─── No-wrap-tilfelle: vakt slutter samme dag (eks. 15-23) ─────────────

    /// <summary>
    /// Bug 2026-05-19: når drifts-leder simulerer kveldsvakt 15:00-23:00
    /// (slutt > start, ingen wrap over midnatt), MÅ ikke modellen tolke
    /// "tid &lt; 23:00" som "fortsatt vakt fra forrige natt" — det ville
    /// gjort hele døgnet vakt og blåst opp Vakt-ROI-totalen.
    /// </summary>
    [Fact]
    public void NoWrap_Kveldsvakt_15_23_TolkerKun_Innenfor_Vinduet()
    {
        var kveldsvakt = VaktTidsmodellOptions.Default with
        {
            EttermiddagStart = new TimeSpan(15, 0, 0),
            MorgenCutoff = new TimeSpan(23, 0, 0), // "slutt" — samme døgn
        };
        var modell = new VaktTidsmodell(kveldsvakt);

        // Onsdag 4. feb 2026 — hverdag
        modell.ErInnenforVakt(OsloLokal(2026, 2, 4, 2))
            .Should().BeFalse("02:00 er natt — ikke vakt når slutt=23:00 samme dag");
        modell.ErInnenforVakt(OsloLokal(2026, 2, 4, 10))
            .Should().BeFalse("10:00 er arbeidstid");
        modell.ErInnenforVakt(OsloLokal(2026, 2, 4, 14, 59))
            .Should().BeFalse("rett før 15:00 = arbeidstid");
        modell.ErInnenforVakt(OsloLokal(2026, 2, 4, 15))
            .Should().BeTrue("15:00 = start kveldsvakt");
        modell.ErInnenforVakt(OsloLokal(2026, 2, 4, 22, 59))
            .Should().BeTrue("rett før 23:00 = fortsatt kveldsvakt");
        modell.ErInnenforVakt(OsloLokal(2026, 2, 4, 23))
            .Should().BeFalse("23:00 = slutt kveldsvakt (eksklusiv)");
        modell.ErInnenforVakt(OsloLokal(2026, 2, 4, 23, 30))
            .Should().BeFalse("23:30 = ingen nattvakt");
    }

    [Fact]
    public void NoWrap_Kveldsvakt_HelgFortsattFullVakt()
    {
        // Helg-håndteringen skal IKKE påvirkes av no-wrap-konfig.
        var kveldsvakt = VaktTidsmodellOptions.Default with
        {
            EttermiddagStart = new TimeSpan(15, 0, 0),
            MorgenCutoff = new TimeSpan(23, 0, 0),
        };
        var modell = new VaktTidsmodell(kveldsvakt);

        // Lørdag 7. feb 2026 kl 03:00 — helg, fortsatt vakt
        modell.ErInnenforVakt(OsloLokal(2026, 2, 7, 3))
            .Should().BeTrue("lørdag = vakt hele døgnet, uavhengig av vakt-vindu");
        // Søndag 8. feb 2026 kl 10:00 — helg
        modell.ErInnenforVakt(OsloLokal(2026, 2, 8, 10))
            .Should().BeTrue("søndag = vakt hele døgnet");
    }

    [Fact]
    public void Wrap_StandardVakt_OverlevdesUendret()
    {
        // Regresjons-sjekk: default-konfig (slutt=07, start=15) wrapper over
        // midnatt — same dag-tester som tidligere.
        var modell = new VaktTidsmodell();
        modell.ErInnenforVakt(OsloLokal(2026, 2, 4, 2))
            .Should().BeTrue("02:00 = fortsatt nattvakt fra forrige kveld");
        modell.ErInnenforVakt(OsloLokal(2026, 2, 4, 6, 59))
            .Should().BeTrue("06:59 = rett før morgen-cutoff = vakt");
        modell.ErInnenforVakt(OsloLokal(2026, 2, 4, 7))
            .Should().BeFalse("07:00 = morgen-cutoff (eksklusiv) → arbeidstid");
        modell.ErInnenforVakt(OsloLokal(2026, 2, 4, 23, 30))
            .Should().BeTrue("23:30 = fortsatt kveldsvakt før midnatt");
    }
}
