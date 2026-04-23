using FluentAssertions;
using KraftverkUptime.Core.Time;
using Xunit;

namespace KraftverkUptime.Core.Tests;

public class TimeZonesTests
{
    [Fact]
    public void Norway_TimeZone_Is_Resolved()
    {
        TimeZones.Norway.Should().NotBeNull();
        TimeZones.Norway.Id.Should().BeOneOf("Europe/Oslo", "W. Europe Standard Time");
    }

    [Fact]
    public void Norway_Handles_Spring_DST_Transition()
    {
        // Siste søndag i mars: kl. 02:00 → 03:00. Vi skal ikke få "ugyldig" tid.
        var localBefore = new DateTime(2026, 3, 29, 1, 30, 0, DateTimeKind.Unspecified);
        var utc = TimeZoneInfo.ConvertTimeToUtc(localBefore, TimeZones.Norway);
        utc.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void Norway_Handles_Fall_DST_Transition()
    {
        // Siste søndag i oktober: 03:00 → 02:00. Lokal 02:30 er tvetydig.
        var localAmbiguous = new DateTime(2026, 10, 25, 2, 30, 0, DateTimeKind.Unspecified);
        TimeZones.Norway.IsAmbiguousTime(localAmbiguous).Should().BeTrue();
    }
}
