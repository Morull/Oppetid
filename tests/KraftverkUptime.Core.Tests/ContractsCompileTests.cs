using FluentAssertions;
using KraftverkUptime.Core.Analysis;
using KraftverkUptime.Core.Configuration;
using KraftverkUptime.Core.DataSources;
using KraftverkUptime.Core.Events;
using KraftverkUptime.Core.Jobs;
using KraftverkUptime.Core.Modules;
using KraftverkUptime.Core.Notifications;
using KraftverkUptime.Core.Paging;
using KraftverkUptime.Core.Reporting;
using KraftverkUptime.Core.Security;
using KraftverkUptime.Core.Storage;
using Xunit;

namespace KraftverkUptime.Core.Tests;

/// <summary>
/// Kompileringstest på at hver kontrakt eksisterer. Hvis en kontrakt fjernes eller endrer signatur,
/// skal denne testen bryte slik at det er åpenbart at det er en breaking change.
/// </summary>
public class ContractsCompileTests
{
    [Fact]
    public void All_Core_Contract_Types_Exist()
    {
        typeof(IDataSource).Should().NotBeNull();
        typeof(ISettlementDataSource).Should().NotBeNull();
        typeof(IScadaDataSource).Should().NotBeNull();
        typeof(IHydrologicalDataSource).Should().NotBeNull();
        typeof(ICmmsDataSource).Should().NotBeNull();
        typeof(IAnalyzer<,>).Should().NotBeNull();
        typeof(IReportBuilder<>).Should().NotBeNull();
        typeof(IReportRenderer).Should().NotBeNull();
        typeof(IReportSink).Should().NotBeNull();
        typeof(ReportRequest).Should().NotBeNull();
        typeof(ICurrentUser).Should().NotBeNull();
        typeof(IQueryContext).Should().NotBeNull();
        typeof(IAuditLogger).Should().NotBeNull();
        typeof(AuthorizationPolicies).Should().NotBeNull();
        typeof(IJobQueue).Should().NotBeNull();
        typeof(IJobHandler<>).Should().NotBeNull();
        typeof(IFileStorage).Should().NotBeNull();
        typeof(IPlantConfiguration).Should().NotBeNull();
        typeof(INotificationService).Should().NotBeNull();
        typeof(IEventPublisher).Should().NotBeNull();
        typeof(IEventHandler<>).Should().NotBeNull();
        typeof(IDomainEvent).Should().NotBeNull();
        typeof(IPlatformModule).Should().NotBeNull();
        typeof(PagedResult<>).Should().NotBeNull();
    }

    [Fact]
    public void AuthorizationPolicies_Has_Five_Named_Policies()
    {
        AuthorizationPolicies.All.Should().BeEquivalentTo(
            new[]
            {
                AuthorizationPolicies.PlantReader,
                AuthorizationPolicies.PlantAnalyst,
                AuthorizationPolicies.PlantAdmin,
                AuthorizationPolicies.OrgAdmin,
                AuthorizationPolicies.SystemAdmin
            });
    }
}
