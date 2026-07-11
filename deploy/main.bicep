// Provisions the Azure target described in ARCHITECTURE.md's "Deployment" section: Container Apps for
// Api/Management/Dashboard, managed Postgres (Flexible Server) and managed Redis (Azure Cache for Redis), plus
// a Key Vault for AzureKeyVaultSecretStore (tenant BYOK credentials — a separate concern from the infra secrets
// below, which are plain Container App secrets, not Key-Vault-backed).
//
// Not deployed/validated against a real subscription from this environment (no Azure account available) — only
// `az bicep build`-validated for syntax. Treat this the same way CLAUDE.md treats AzureKeyVaultSecretStore:
// "reasonably confident it's structurally correct, but it hasn't been verified end-to-end". See deploy/README.md
// before running it for real.

@description('Short, unique prefix for resource names (e.g. "aigw-prod"). Keep it short — Postgres/Redis/Key Vault names have tight length limits.')
@minLength(3)
@maxLength(12)
param namePrefix string

param location string = resourceGroup().location

@description('Container image references, e.g. ghcr.io/<owner>/ai-gateway-api:<tag> — populated by the CD workflow.')
param apiImage string
param managementImage string
param dashboardImage string

@description('GHCR credentials for pulling the images above, if the repository is private. Leave blank for a public GHCR repo.')
param containerRegistryServer string = 'ghcr.io'
param containerRegistryUsername string = ''
@secure()
param containerRegistryPassword string = ''

@secure()
param postgresAdminPassword string
param postgresAdminLogin string = 'ai_gateway_admin'

@description('"StaticKey" (dev/test only — see Core/Auth doc comments) or "OidcAuthority" (a real IdP).')
@allowed(['StaticKey', 'OidcAuthority'])
param authenticationMode string = 'OidcAuthority'
param oidcAuthority string = ''
@secure()
param staticKeySigningKey string = ''

@description('OpenTelemetry exporter — "Console" (logs go to Container Apps\' own log stream / Log Analytics, no collector needed) or "Otlp" (a real collector).')
@allowed(['Console', 'Otlp'])
param observabilityExporter string = 'Console'
param otlpEndpoint string = ''

var postgresDatabaseName = 'ai_gateway'
var postgresConnectionString = 'Host=${postgres.properties.fullyQualifiedDomainName};Port=5432;Database=${postgresDatabaseName};Username=${postgresAdminLogin};Password=${postgresAdminPassword};Ssl Mode=Require'
var redisConnectionString = '${redis.properties.hostName}:${redis.properties.sslPort},password=${redis.listKeys().primaryKey},ssl=True,abortConnect=False'

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${namePrefix}-logs'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource containerAppsEnv 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${namePrefix}-env'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
  }
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: '${namePrefix}-kv'
  location: location
  properties: {
    sku: { family: 'A', name: 'standard' }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
  }
}

resource postgres 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: '${namePrefix}-pg'
  location: location
  sku: {
    name: 'Standard_B1ms'
    tier: 'Burstable'
  }
  properties: {
    version: '17'
    administratorLogin: postgresAdminLogin
    administratorLoginPassword: postgresAdminPassword
    storage: { storageSizeGB: 32 }
    backup: { backupRetentionDays: 7 }
  }

  resource database 'databases@2024-08-01' = {
    name: postgresDatabaseName
  }

  // Container Apps' outbound IPs aren't static without a NAT gateway/VNet integration (a further deployment
  // hardening step, not built here) — allowing all Azure services is the pragmatic default for this stage. See
  // CLAUDE.md's "Deployment" section for the caveat.
  resource allowAzureServices 'firewallRules@2024-08-01' = {
    name: 'AllowAllAzureServices'
    properties: {
      startIpAddress: '0.0.0.0'
      endIpAddress: '0.0.0.0'
    }
  }
}

resource redis 'Microsoft.Cache/redis@2024-11-01' = {
  name: '${namePrefix}-redis'
  location: location
  properties: {
    sku: { name: 'Basic', family: 'C', capacity: 0 }
    enableNonSslPort: false
    minimumTlsVersion: '1.2'
  }
}

var registryCredentials = empty(containerRegistryUsername)
  ? []
  : [
      {
        server: containerRegistryServer
        username: containerRegistryUsername
        passwordSecretRef: 'registry-password'
      }
    ]

var sharedSecrets = concat(
  [
    { name: 'db-connection-string', value: postgresConnectionString }
    { name: 'redis-connection-string', value: redisConnectionString }
  ],
  empty(containerRegistryUsername) ? [] : [{ name: 'registry-password', value: containerRegistryPassword }],
  authenticationMode == 'StaticKey' ? [{ name: 'static-key-signing-key', value: staticKeySigningKey }] : []
)

var authenticationEnv = authenticationMode == 'StaticKey'
  ? [
      { name: 'Authentication__Mode', value: 'StaticKey' }
      { name: 'Authentication__StaticKey__Issuer', value: '${namePrefix}-issuer' }
      { name: 'Authentication__StaticKey__SigningKey', secretRef: 'static-key-signing-key' }
    ]
  : [
      { name: 'Authentication__Mode', value: 'OidcAuthority' }
      { name: 'Authentication__Authority', value: oidcAuthority }
    ]

var observabilityEnv = observabilityExporter == 'Otlp'
  ? [
      { name: 'Observability__Exporter', value: 'Otlp' }
      { name: 'Observability__OtlpEndpoint', value: otlpEndpoint }
    ]
  : [{ name: 'Observability__Exporter', value: 'Console' }]

resource api 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${namePrefix}-api'
  location: location
  properties: {
    managedEnvironmentId: containerAppsEnv.id
    configuration: {
      ingress: { external: true, targetPort: 8080, transport: 'auto' }
      registries: registryCredentials
      secrets: sharedSecrets
    }
    template: {
      containers: [
        {
          name: 'api'
          image: apiImage
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: concat(
            [
              { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
              { name: 'ConnectionStrings__Gateway', secretRef: 'db-connection-string' }
              { name: 'Secrets__Provider', value: 'AzureKeyVault' }
              { name: 'Secrets__AzureKeyVault__VaultUri', value: keyVault.properties.vaultUri }
              { name: 'RateLimiting__Store', value: 'Redis' }
              { name: 'RateLimiting__RedisConnectionString', secretRef: 'redis-connection-string' }
            ],
            authenticationEnv,
            observabilityEnv
          )
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 5 }
    }
  }
  identity: { type: 'SystemAssigned' }
}

resource management 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${namePrefix}-management'
  location: location
  properties: {
    managedEnvironmentId: containerAppsEnv.id
    configuration: {
      ingress: { external: true, targetPort: 8080, transport: 'auto' }
      registries: registryCredentials
      secrets: sharedSecrets
    }
    template: {
      containers: [
        {
          name: 'management'
          image: managementImage
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: concat(
            [
              { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
              { name: 'ConnectionStrings__Gateway', secretRef: 'db-connection-string' }
              { name: 'Secrets__Provider', value: 'AzureKeyVault' }
              { name: 'Secrets__AzureKeyVault__VaultUri', value: keyVault.properties.vaultUri }
              { name: 'Sessions__Store', value: 'Redis' }
              { name: 'Sessions__RedisConnectionString', secretRef: 'redis-connection-string' }
              { name: 'Sessions__CookieSecure', value: 'true' }
              // No Cors:AllowedOrigins needed — Dashboard's nginx reverse-proxies same-origin (see
              // Dashboard/nginx.conf.template), the same choice local dev makes via the Vite proxy.
            ],
            authenticationEnv,
            observabilityEnv
          )
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 5 }
    }
  }
  identity: { type: 'SystemAssigned' }
}

resource dashboard 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${namePrefix}-dashboard'
  location: location
  properties: {
    managedEnvironmentId: containerAppsEnv.id
    configuration: {
      ingress: { external: true, targetPort: 8080, transport: 'auto' }
      registries: registryCredentials
    }
    template: {
      containers: [
        {
          name: 'dashboard'
          image: dashboardImage
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: [
            { name: 'MANAGEMENT_UPSTREAM', value: 'https://${management.properties.configuration.ingress.fqdn}' }
          ]
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 3 }
    }
  }
}

// Api and Management read/write tenant BYOK credentials via AzureKeyVaultSecretStore (Core/Secrets) — grant
// their managed identities Key Vault Secrets Officer (get/set/delete, matching ISecretStore's three operations)
// rather than a broader role.
var keyVaultSecretsOfficerRoleId = 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'

resource apiKeyVaultAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, api.id, keyVaultSecretsOfficerRoleId)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsOfficerRoleId)
    principalId: api.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource managementKeyVaultAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, management.id, keyVaultSecretsOfficerRoleId)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsOfficerRoleId)
    principalId: management.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

output apiUrl string = 'https://${api.properties.configuration.ingress.fqdn}'
output managementUrl string = 'https://${management.properties.configuration.ingress.fqdn}'
output dashboardUrl string = 'https://${dashboard.properties.configuration.ingress.fqdn}'
output keyVaultUri string = keyVault.properties.vaultUri
