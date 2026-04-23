using Microsoft.Extensions.DependencyInjection;

namespace KraftverkUptime.Core.Modules;

/// <summary>
/// Hvert domenemodul-prosjekt eksponerer én IPlatformModule som registrerer egne tjenester.
/// Composition root i Api/Worker/Web kaller RegisterServices på alle moduler de refererer.
/// Oppgradering av én modul = én linje i Program.cs (bytt til ny modul-klasse).
/// </summary>
public interface IPlatformModule
{
    string Name { get; }
    int SchemaVersion { get; }

    void RegisterServices(IServiceCollection services);
}
