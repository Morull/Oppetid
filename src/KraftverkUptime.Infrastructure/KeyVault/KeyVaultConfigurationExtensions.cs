using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Configuration;

namespace KraftverkUptime.Infrastructure.KeyVault;

/// <summary>
/// Legger Azure Key Vault som config-provider når KEYVAULT_URL er satt.
/// DefaultAzureCredential faller tilbake til Azure CLI i dev (MI fungerer ikke lokalt).
/// Secret-caching gjøres av selve provideren (ReloadInterval = 15 min) for å unngå throttling.
/// </summary>
public static class KeyVaultConfigurationExtensions
{
    public static IConfigurationBuilder AddKraftverkKeyVault(this IConfigurationBuilder builder, IConfiguration? existing = null)
    {
        var keyVaultUrl = Environment.GetEnvironmentVariable("KEYVAULT_URL")
            ?? existing?["KeyVault:Url"];

        if (string.IsNullOrWhiteSpace(keyVaultUrl))
        {
            return builder;
        }

        var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ExcludeInteractiveBrowserCredential = true,
            ExcludeVisualStudioCredential = false
        });

        var client = new SecretClient(new Uri(keyVaultUrl), credential);
        builder.AddAzureKeyVault(client, new Azure.Extensions.AspNetCore.Configuration.Secrets.AzureKeyVaultConfigurationOptions
        {
            ReloadInterval = TimeSpan.FromMinutes(15) // Fallgrube: throttling ved høyfrekvent oppslag.
        });
        return builder;
    }
}
