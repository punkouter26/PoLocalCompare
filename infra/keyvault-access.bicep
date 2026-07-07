targetScope = 'resourceGroup'

@description('Principal id of the App Service system-assigned identity')
param principalId string

@description('Shared Key Vault name in this resource group')
param keyVaultName string

// Key Vault Secrets User (standards §5.4). Effective once kv-poshared runs in RBAC mode;
// harmless (ignored for data plane) while the vault still uses access policies.
var keyVaultSecretsUserRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '4633458b-17de-408a-b874-0445c86b69e6'
)

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource secretsUserAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, principalId, keyVaultSecretsUserRoleId)
  scope: keyVault
  properties: {
    roleDefinitionId: keyVaultSecretsUserRoleId
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}

// kv-poshared currently runs in ACCESS POLICY mode, so also grant get/list directly —
// this is what actually authorizes secret reads today.
resource accessPolicy 'Microsoft.KeyVault/vaults/accessPolicies@2023-07-01' = {
  parent: keyVault
  name: 'add'
  properties: {
    accessPolicies: [
      {
        tenantId: subscription().tenantId
        objectId: principalId
        permissions: {
          secrets: ['get', 'list']
        }
      }
    ]
  }
}
