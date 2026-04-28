using KraftverkUptime.Core.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace KraftverkUptime.Modules.Scada;

/// <summary>
/// DI-registrering for SCADA-modulen. Repository-implementasjoner ligger i
/// Infrastructure (EF Core); registreres i AddKraftverkInfrastructure for å
/// unngå sirkulære prosjekt-referanser. Her registreres kun analyzer-/
/// klassifikator-tjenester når de bygges.
/// </summary>
public sealed class ScadaModule : IPlatformModule
{
    public string Name => "Scada";
    public int SchemaVersion => 1;

    public void RegisterServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Foreløpig ingen tjenester her — repositoriene registreres i
        // Infrastructure. ScadaClassifier + FusionClassifier legges til
        // når SCADA-data er på plass.
    }
}
