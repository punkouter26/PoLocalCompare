targetScope = 'resourceGroup'

@description('Azure region (matches the PoLocalCompare resource group)')
param location string = 'westus2'

@description('Name of the new App Service to create on the existing plan')
param appServiceName string = 'app-polocalcompare-win'

// ─── Shared platform resources (resource group: PoShared) ──────────────────────────
@description('Shared resource group holding Key Vault')
param sharedResourceGroupName string = 'PoShared'

resource sharedKeyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: 'kv-poshared'
  scope: resourceGroup(sharedResourceGroupName)
}

// ─── Existing resources in this resource group (PoLocalCompare) ─────────────────────
// Storage already exists; the app creates its tables at runtime (CreateIfNotExists) and
// connects with its system-assigned managed identity (standards §5.4).
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
    // System-assigned managed identity (standards §5.4) — replaces the shared
    // user-assigned mi-poshared-containerapps.
    type: 'SystemAssigned'
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
          // Identity-based storage access: the app resolves Table/Blob endpoints from the
          // account name via DefaultAzureCredential instead of a shared-key connection string.
          name: 'AzureStorage__AccountName'
          value: storageAccount.name
        }
      ]
    }
  }
}

// ─── Storage RBAC for the system-assigned identity ─────────────────────────────────
var storageTableDataContributor = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
)
var storageBlobDataContributor = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
)

resource tableRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, appService.id, storageTableDataContributor)
  scope: storageAccount
  properties: {
    roleDefinitionId: storageTableDataContributor
    principalId: appService.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource blobRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, appService.id, storageBlobDataContributor)
  scope: storageAccount
  properties: {
    roleDefinitionId: storageBlobDataContributor
    principalId: appService.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// ─── Key Vault access for the system-assigned identity (PoShared RG) ───────────────
module keyVaultAccess 'keyvault-access.bicep' = {
  name: 'polocalcompare-kv-access'
  scope: resourceGroup(sharedResourceGroupName)
  params: {
    principalId: appService.identity.principalId
    keyVaultName: sharedKeyVault.name
  }
}

// NOTE: add `PoLocalCompare--AzureAd--ClientSecret` to kv-poshared for the BFF OIDC sign-in.

// ─── Outputs ────────────────────────────────────────────────────────────────────────
output appServiceName string = appService.name
output appServiceUrl string = 'https://${appService.properties.defaultHostName}'
output storageAccountName string = storageAccount.name
output appServicePrincipalId string = appService.identity.principalId
