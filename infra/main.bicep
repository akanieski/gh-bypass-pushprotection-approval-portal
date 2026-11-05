targetScope = 'resourceGroup'

@description('Lowercase Container App name, starting with a letter.')
@minLength(3)
@maxLength(20)
param name string = 'approval-portal'
param location string = resourceGroup().location
@description('Organization label shown before Bypass Push Protection Approvals.')
param organizationSlug string = 'Customer'

@description('Published image accessible without registry credentials. Prefer an immutable digest.')
param image string
param githubHost string = 'github.com'
param githubAppId string
param githubInstallationId int
param githubOAuthClientId string
param repository string = ''

@description('Bootstrap approver IDs, overridden by persistent config.json. At least two distinct people.')
param approverIds string = ''
@minValue(2)
param requiredApprovals int = 2

@secure()
param githubOAuthSecret string
@secure()
@description('Complete PEM private key, including newlines.')
param githubAppPrivateKey string
@secure()
param teamsWebhookUrl string = ''

@minValue(5)
@maxValue(3600)
param pollSeconds int = 60

resource storage 'Microsoft.Storage/storageAccounts@2025-01-01' = {
  name: 'ap${uniqueString(resourceGroup().id, name)}'
  location: location
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    allowSharedKeyAccess: true
  }
}

resource fileService 'Microsoft.Storage/storageAccounts/fileServices@2025-01-01' = {
  parent: storage
  name: 'default'
}

resource share 'Microsoft.Storage/storageAccounts/fileServices/shares@2025-01-01' = {
  parent: fileService
  name: 'portal-state'
  properties: {
    enabledProtocols: 'SMB'
    shareQuota: 5
  }
}

resource environment 'Microsoft.App/managedEnvironments@2025-01-01' = {
  name: '${name}-env'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'azure-monitor'
    }
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
  }
}

resource environmentStorage 'Microsoft.App/managedEnvironments/storages@2025-01-01' = {
  parent: environment
  name: 'portal-state'
  properties: {
    azureFile: {
      accountName: storage.name
      accountKey: storage.listKeys().keys[0].value
      shareName: share.name
      accessMode: 'ReadWrite'
    }
  }
}

var publicOrigin = 'https://${name}.${environment.properties.defaultDomain}'
var settings = {
  ASPNETCORE_HTTP_PORTS: '8080'
  ASPNETCORE_FORWARDEDHEADERS_ENABLED: 'true'
  Portal__Enabled: 'true'
  Portal__OrganizationSlug: organizationSlug
  Portal__GitHubHost: githubHost
  Portal__AppId: githubAppId
  Portal__InstallationId: string(githubInstallationId)
  Portal__OAuthClientId: githubOAuthClientId
  Portal__AppPrivateKeyFile: '/run/secrets/github-app.pem'
  Portal__Repositories__0: repository
  Portal__ApproverIdsCsv: approverIds
  Portal__RequiredApprovals: string(requiredApprovals)
  Portal__PollSeconds: string(pollSeconds)
  Portal__StorageProvider: 'FileSystem'
  Portal__DataDirectory: '/data'
}
var environmentVariables = [for setting in items(settings): {
  name: setting.key
  value: setting.value
}]

resource app 'Microsoft.App/containerApps@2025-01-01' = {
  name: name
  location: location
  properties: {
    managedEnvironmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        allowInsecure: false
        targetPort: 8080
        transport: 'http'
      }
      secrets: concat([
        {
          name: 'oauth-secret'
          value: githubOAuthSecret
        }
        {
          name: 'github-app-key'
          value: githubAppPrivateKey
        }
      ], empty(teamsWebhookUrl) ? [] : [
        {
          name: 'teams-webhook'
          value: teamsWebhookUrl
        }
      ])
    }
    template: {
      scale: {
        minReplicas: 1
        maxReplicas: 1
      }
      containers: [
        {
          name: 'portal'
          image: image
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: concat(environmentVariables, [
            {
              name: 'Portal__PublicOrigin'
              value: publicOrigin
            }
            {
              name: 'Portal__OAuthClientSecret'
              secretRef: 'oauth-secret'
            }
          ], empty(teamsWebhookUrl) ? [] : [
            {
              name: 'Portal__TeamsWebhookUrl'
              secretRef: 'teams-webhook'
            }
          ])
          volumeMounts: [
            {
              volumeName: 'state'
              mountPath: '/data'
            }
            {
              volumeName: 'credentials'
              mountPath: '/run/secrets'
            }
          ]
          probes: [
            {
              type: 'Readiness'
              httpGet: {
                path: '/health'
                port: 8080
              }
              initialDelaySeconds: 5
              periodSeconds: 10
            }
          ]
        }
      ]
      volumes: [
        {
          name: 'state'
          storageType: 'AzureFile'
          storageName: environmentStorage.name
          mountOptions: 'uid=1654,gid=1654,dir_mode=0700,file_mode=0600,cache=strict,actimeo=0'
        }
        {
          name: 'credentials'
          storageType: 'Secret'
          secrets: [
            {
              secretRef: 'github-app-key'
              path: 'github-app.pem'
            }
          ]
        }
      ]
    }
  }
}

output portalUrl string = '${publicOrigin}/approvals'
output oauthCallbackUrl string = '${publicOrigin}/signin-github'
