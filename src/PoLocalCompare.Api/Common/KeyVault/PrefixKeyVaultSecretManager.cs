using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Configuration;

namespace PoLocalCompare.Api.Infrastructure;

/// <summary>
/// Filters Key Vault secrets to those matching the app prefix and strips the prefix
/// so they map to the same configuration keys used locally.
///
/// Example:  KV secret "PoLocalCompare--ConnectionStrings--AzureTableStorage"
///           → IConfiguration key "ConnectionStrings:AzureTableStorage"
/// </summary>
internal sealed class PrefixKeyVaultSecretManager : KeyVaultSecretManager
{
    private readonly string _prefix;

    public PrefixKeyVaultSecretManager(string appName)
        => _prefix = appName + "--";

    public override bool Load(SecretProperties secret)
        => secret.Name.StartsWith(_prefix, StringComparison.Ordinal);

    public override string GetKey(KeyVaultSecret secret)
        => secret.Name[_prefix.Length..]
                 .Replace("--", ConfigurationPath.KeyDelimiter);
}
