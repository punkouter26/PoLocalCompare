targetScope = 'resourceGroup'

@description('Azure region (matches the PoLocalCompare resource group)')
param location string = 'westus2'

@description('Name of the new App Service to create on the existing plan')
param appServiceName string = 'app-polocalcompare-win'

// ─── Shared platform resources (resource group: PoShared) ──────────────────────────
@description('Shared resource group holding Key Vault, App Insights, and the workload identity')
param sharedResourceGroupName string = 'PoShared'

resource sharedKeyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: 'kv-poshared'
  scope: resourceGroup(sharedResourceGroupName)
}

resource sharedAppInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: 'poappideinsights8f9c9a4e'
  scope: resourceGroup(sharedResourceGroupName)
}

resource sharedManagedIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: 'mi-poshared-containerapps'
  scope: resourceGroup(sharedResourceGroupName)
}

// ─── Existing resources in this resource group (PoLocalCompare) ─────────────────────
// Storage already exists; the app creates its tables at runtime (CreateIfNotExists) and
// connects with the shared-key connection string from Key Vault
// (PoLocalCompare--ConnectionStrings--AzureTableStorage), so we only reference it here.
resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: 'polocalcomparedevsa'
}

// Existing Windows Free (F1) plan in this resource group.
resource appServicePlan 'Microsoft.Web/serverfarms@2023-12-01' existing = {
  name: 'asp-PoLocalCompare-f1'
}

// ─── App Service (Windows, .NET 10) ────────────────────────────────────────────────
resource appService 'Microsoft.Web/sites@2023-12-01' = {
  name: appServiceName
  location: location
  kind: 'app'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${sharedManagedIdentity.id}': {}
    }
  }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      // F1 (Free) does not support AlwaysOn.
      alwaysOn: false
      netFrameworkVersion: 'v10.0'
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      http20Enabled: true
      metadata: [
        {
          name: 'CURRENT_STACK'
          value: 'dotnet'
        }
      ]
      appSettings: [
        {
          name: 'ASPNETCORE_ENVIRONMENT'
          value: 'Production'
        }
        {
          // CI ships a built publish output; skip Oryx/Kudu build on deploy.
          name: 'SCM_DO_BUILD_DURING_DEPLOYMENT'
          value: 'false'
        }
        {
          name: 'KeyVault__Uri'
          value: sharedKeyVault.properties.vaultUri
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: sharedAppInsights.properties.ConnectionString
        }
        {
          // Targets the shared user-assigned identity for DefaultAzureCredential (Key Vault access).
          name: 'AZURE_CLIENT_ID'
          value: sharedManagedIdentity.properties.clientId
        }
      ]
    }
  }
}

// Key Vault access: kv-poshared uses ACCESS POLICIES (not RBAC) and the shared identity already
// holds a get/list secrets policy — so no role assignment is managed here. Storage uses the
// Key Vault connection string (shared key), so no Storage RBAC role is needed either.
// NOTE: add `PoLocalCompare--AzureAd--ClientSecret` to kv-poshared for the BFF OIDC sign-in.

// ─── Outputs ────────────────────────────────────────────────────────────────────────
output appServiceName string = appService.name
output appServiceUrl string = 'https://${appService.properties.defaultHostName}'
output storageAccountName string = storageAccount.name
