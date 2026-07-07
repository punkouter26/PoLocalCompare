using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace PoLocalCompare.Api.Common.KeyVault;

public static class KeyVaultExtensions
{
    public static IServiceCollection AddKeyVaultSecrets(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var keyVaultUri = configuration["KeyVault:Uri"];
        if (!string.IsNullOrEmpty(keyVaultUri))
        {
            var credential = new DefaultAzureCredential();
            services.AddSingleton(new SecretClient(new Uri(keyVaultUri), credential));
        }
        return services;
    }
}
