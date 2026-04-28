using KraftverkUptime.Core.Modules;
using KraftverkUptime.Modules.Annotations.Overlay;
using Microsoft.Extensions.DependencyInjection;

namespace KraftverkUptime.Modules.Annotations;

/// <summary>
/// DI-registrering for annoterings-modulen. Repository-implementasjonene lever
/// i Infrastructure (EF Core), så de registreres i AddKraftverkInfrastructure
/// for å unngå sirkulære prosjekt-referanser.
/// </summary>
public sealed class AnnotationsModule : IPlatformModule
{
    public string Name => "Annotations";
    public int SchemaVersion => 1;

    public void RegisterServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Overlay er statless og kan trygt være Singleton.
        services.AddSingleton<AnnotationOverlayService>();
    }
}
