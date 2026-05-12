targetScope = 'resourceGroup'

@description('Environment name (dev, staging, prod)')
param environmentName string = 'dev'

@description('Azure region for resources')
param location string = resourceGroup().location

@description('Resource group name for PoShared shared resources (App Service Plan)')
param sharedResourceGroupName string = 'PoShared'

@description('App Service Plan name in PoShared resource group')
param sharedAppServicePlanName string = 'asp-poshared-linux'

resource sharedKeyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: 'kv-poshared'
  scope: resourceGroup(sharedResourceGroupName)
}

// ─── Storage Account ──────────────────────────────────────────────────────────
resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'polocalcompare${environmentName}sa'
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    supportsHttpsTrafficOnly: true
  }
}

// ─── Table Storage Service ─────────────────────────────────────────────────────
resource tableService 'Microsoft.Storage/storageAccounts/tableServices@2023-05-01' = {
  parent: storageAccount
  name: 'default'
}

resource modelsTable 'Microsoft.Storage/storageAccounts/tableServices/tables@2023-05-01' = {
  parent: tableService
  name: 'Models'
}

resource duelsTable 'Microsoft.Storage/storageAccounts/tableServices/tables@2023-05-01' = {
  parent: tableService
  name: 'Duels'
}

resource duelResultsTable 'Microsoft.Storage/storageAccounts/tableServices/tables@2023-05-01' = {
  parent: tableService
  name: 'DuelResults'
}

resource eloHistoryTable 'Microsoft.Storage/storageAccounts/tableServices/tables@2023-05-01' = {
  parent: tableService
  name: 'EloHistory'
}

// ─── Blob Storage ─────────────────────────────────────────────────────────────
resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storageAccount
  name: 'default'
}

resource duelHtmlOutputsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'duel-html-outputs'
  properties: {
    publicAccess: 'None'
  }
}

// ─── Linux App Service (shared plan in PoShared) ─────────────────────────────
resource sharedAppServicePlan 'Microsoft.Web/serverfarms@2023-12-01' existing = {
  name: sharedAppServicePlanName
  scope: resourceGroup(sharedResourceGroupName)
}

resource appService 'Microsoft.Web/sites@2023-12-01' = {
  name: 'PoLocalCompare-AppService-${environmentName}'
  location: location
  kind: 'app,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: sharedAppServicePlan.id
    httpsOnly: true
    siteConfig: {
      alwaysOn: true
      appCommandLine: 'dotnet PoLocalCompare.Api.dll'
      appSettings: [
        {
          name: 'ASPNETCORE_ENVIRONMENT'
          value: 'Production'
        }
        {
          name: 'ASPNETCORE_URLS'
          value: 'http://+:8080'
        }
        {
          name: 'KeyVault__Uri'
          value: sharedKeyVault.properties.vaultUri
        }
        {
          name: 'ConnectionStrings__AzureTableStorage'
          value: '@Microsoft.KeyVault(SecretUri=${sharedKeyVault.properties.vaultUri}secrets/PoLocalCompare--ConnectionStrings--AzureTableStorage/)'
        }
        {
          name: 'ConnectionStrings__AzureBlobStorage'
          value: '@Microsoft.KeyVault(SecretUri=${sharedKeyVault.properties.vaultUri}secrets/PoLocalCompare--ConnectionStrings--AzureBlobStorage/)'
        }
      ]
      ftpsState: 'Disabled'
      healthCheckPath: '/health'
      linuxFxVersion: 'DOTNETCORE|10.0'
      minTlsVersion: '1.2'
      http20Enabled: true
    }
  }
}

// ─── RBAC: Storage Table Data Contributor ────────────────────────────────────
@description('Storage Table Data Contributor role')
var storageTableDataContributorRoleId = '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'

resource tableRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, appService.id, storageTableDataContributorRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageTableDataContributorRoleId)
    principalId: appService.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// ─── RBAC: Storage Blob Data Contributor ─────────────────────────────────────
@description('Storage Blob Data Contributor role')
var storageBlobDataContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'

resource blobRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, appService.id, storageBlobDataContributorRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributorRoleId)
    principalId: appService.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// ─── Outputs ─────────────────────────────────────────────────────────────────
output storageAccountName string = storageAccount.name
output appServiceName string = appService.name
output appServiceUrl string = 'https://${appService.properties.defaultHostName}'
