# PoLocalCompare CI/CD Modernization Implementation Guide

## Quick Start Implementation Order

### Phase 1: Foundation (Week 1)
**Focus**: Security & Visibility  
**Effort**: ~2 hours total

#### Step 1: Add Security Scanning to CI/CD
File: `.github/workflows/deploy-to-azure.yml`

Add after the "Build" step:

```yaml
      - name: Install Trivy
        run: |
          wget -qO - https://aquasecurity.github.io/trivy-repo/deb/public.key | apt-key add -
          echo "deb https://aquasecurity.github.io/trivy-repo/deb $(lsb_release -sc) main" | tee /etc/apt/sources.list.d/trivy.list
          apt-get update && apt-get install -y trivy

      - name: Run Trivy vulnerability scan
        run: |
          trivy fs --exit-code 0 --severity HIGH,CRITICAL --format json --output trivy-results.json .

      - name: Check for secrets
        run: |
          dotnet list package --vulnerable --include-transitive
```

#### Step 2: Enable Application Insights
File: `infra/main.bicep`

Add near the top after parameters:

```bicep
resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'ai-polocalcompare-${environmentName}'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    RetentionInDays: 30
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

resource appServiceDiagnostics 'Microsoft.Insights/diagnosticSettings@2017-05-01-preview' = {
  name: 'polocalcompare-diagnostics'
  scope: appService
  properties: {
    workspaceId: ''  // Link to Log Analytics if available
    metrics: [
      {
        category: 'AllMetrics'
        enabled: true
        retentionPolicy: {
          days: 30
          enabled: true
        }
      }
    ]
    logs: [
      {
        category: 'AppServiceHTTPLogs'
        enabled: true
        retentionPolicy: {
          days: 30
          enabled: true
        }
      }
    ]
  }
}
```

Update App Service settings to include:
```bicep
{
  name: 'APPINSIGHTS_INSTRUMENTATIONKEY'
  value: appInsights.properties.InstrumentationKey
}
{
  name: 'ApplicationInsightsAgent_EXTENSION_VERSION'
  value: '~3'
}
```

#### Step 3: Update .gitignore
✅ **Already Done** in this session

### Phase 2: Scalability (Week 2)
**Focus**: Multi-environment support  
**Effort**: ~3 hours total

#### Step 1: Create Environment Configurations
Create files:
- `infra/parameters/dev.bicepparam`
- `infra/parameters/staging.bicepparam`
- `infra/parameters/prod.bicepparam`

Example `dev.bicepparam`:
```bicep
using './main.bicep'

param environmentName = 'dev'
param location = 'westus2'
param sharedResourceGroupName = 'PoShared'
param sharedAppServicePlanName = 'asp-poshared-linux'
```

#### Step 2: Update CI/CD Workflow
Modify `.github/workflows/deploy-to-azure.yml`:

```yaml
on:
  push:
    branches:
      - master
      - develop
      - staging
  workflow_dispatch:
    inputs:
      environment:
        description: 'Target environment'
        required: true
        default: 'dev'
        type: choice
        options:
          - dev
          - staging
          - prod

jobs:
  determine-environment:
    runs-on: ubuntu-latest
    outputs:
      environment: ${{ steps.env.outputs.environment }}
    steps:
      - id: env
        run: |
          if [[ "${{ github.ref }}" == "refs/heads/master" ]]; then
            echo "environment=prod" >> $GITHUB_OUTPUT
          elif [[ "${{ github.ref }}" == "refs/heads/staging" ]]; then
            echo "environment=staging" >> $GITHUB_OUTPUT
          else
            echo "environment=dev" >> $GITHUB_OUTPUT
          fi

  build-test-deploy:
    needs: determine-environment
    environment: ${{ needs.determine-environment.outputs.environment }}
    runs-on: ubuntu-latest
    env:
      AZURE_ENVIRONMENT_NAME: ${{ needs.determine-environment.outputs.environment }}
      AZURE_RESOURCE_GROUP: PoLocalCompare-${{ needs.determine-environment.outputs.environment }}
    steps:
      # ... existing steps, but use ${{ env.AZURE_ENVIRONMENT_NAME }}
```

### Phase 3: Performance (Week 3)
**Focus**: Blazor deployment to Static Web Apps  
**Effort**: ~4 hours total

#### Step 1: Create SWA Deployment Workflow
Create `.github/workflows/deploy-static-web-app.yml`:

```yaml
name: Deploy Blazor Client to Static Web Apps

on:
  push:
    branches: [master]
    paths:
      - 'src/Client/PoLocalCompare.Client/**'
  workflow_dispatch:

permissions:
  contents: read
  id-token: write

env:
  DOTNET_VERSION: 10.0.x

jobs:
  deploy:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
        with:
          fetch-depth: 0

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: ${{ env.DOTNET_VERSION }}

      - name: Restore WebAssembly workload
        run: dotnet workload restore

      - name: Publish Blazor
        run: |
          dotnet publish src/Client/PoLocalCompare.Client/PoLocalCompare.Client.csproj \
            --configuration Release \
            --output ./publish/wwwroot

      - name: Deploy to Static Web Apps
        uses: Azure/static-web-apps-deploy@v1
        with:
          azure_static_web_apps_api_token: ${{ secrets.AZURE_STATIC_WEB_APPS_TOKEN }}
          repo_token: ${{ secrets.GITHUB_TOKEN }}
          action: "upload"
          app_location: "publish/wwwroot"
          skip_app_build: true
```

#### Step 2: Create API Proxy Rules
Create `staticwebapp.config.json` in `src/Client/PoLocalCompare.Client/wwwroot/`:

```json
{
  "navigationFallback": {
    "rewrite": "index.html"
  },
  "routes": [
    {
      "route": "/api/*",
      "allowedRoles": ["anonymous"],
      "rewrite": "https://polocalcompare-api.azurewebsites.net/api/*"
    },
    {
      "route": "/health",
      "rewrite": "https://polocalcompare-api.azurewebsites.net/health"
    }
  ],
  "mimeTypes": {
    ".wasm": "application/wasm"
  }
}
```

### Phase 4: Optimization (Week 4)
**Focus**: Build performance  
**Effort**: ~1-2 hours total

#### Step 1: Enable Docker BuildKit Caching
Add to main deployment workflow:

```yaml
      - name: Set up Docker Buildx
        uses: docker/setup-buildx-action@v3

      - name: Login to Azure Container Registry
        uses: azure/docker-login@v1
        with:
          login-server: ${{ secrets.REGISTRY_LOGIN_SERVER }}
          username: ${{ secrets.REGISTRY_USERNAME }}
          password: ${{ secrets.REGISTRY_PASSWORD }}

      - name: Build and push with cache
        uses: docker/build-push-action@v5
        with:
          context: src/PoLocalCompare.Api
          file: src/PoLocalCompare.Api/Dockerfile
          cache-from: type=registry,ref=${{ secrets.REGISTRY_LOGIN_SERVER }}/polocalcompare:buildcache
          cache-to: type=registry,ref=${{ secrets.REGISTRY_LOGIN_SERVER }}/polocalcompare:buildcache,mode=max
          push: true
          tags: ${{ secrets.REGISTRY_LOGIN_SERVER }}/polocalcompare:latest
```

---

## Validation Checklist After Each Phase

### After Phase 1: Security
- [ ] Trivy scan runs and reports vulnerabilities
- [ ] Application Insights shows metrics in Azure Portal
- [ ] No secrets detected in scan results
- [ ] GitHub security alerts integration working

### After Phase 2: Multi-Environment
- [ ] Develop branch deploys to dev environment
- [ ] Staging branch deploys to staging environment
- [ ] Master branch deploys to prod environment
- [ ] Environment-specific configurations load correctly

### After Phase 3: Frontend Optimization
- [ ] Blazor client deploys to Static Web Apps
- [ ] API proxy rules work for `/api/*` calls
- [ ] Static Web Apps shows correct deployment status
- [ ] Performance metrics improve (CDN, static asset delivery)

### After Phase 4: Build Performance
- [ ] Build time reduces from 8min to 5min
- [ ] Docker layer caching is utilized
- [ ] No rebuilds of unchanged layers

---

## Cost Impact Analysis

| Recommendation | Service | Current Cost | Projected | Savings |
|---|---|---|---|---|
| #1: SWA for Blazor | Static Web Apps | $0 | Free tier | -$0 |
| #3: App Insights | Application Insights | $0 | $5-10/mo | +$5-10 |
| #2: Multi-env | App Service Plan | ~$50 | ~$120 | -$70 |
| **Total Impact** | | | | ~-$65/mo |

> **Note**: Multi-environment adds cost but provides critical production stability.  
> Cost can be optimized by using dev/staging on lower tiers.

---

## Success Metrics

Track these KPIs after implementation:

| Metric | Current | Target | Timeline |
|--------|---------|--------|----------|
| Build Duration | 8 min | 5 min | Week 4 |
| Deployment Frequency | 1x/week | 2x/week | Week 2 |
| Time to Recovery | N/A | <15 min | Week 2 |
| Security Scan Pass Rate | 0% | 100% | Week 1 |
| App Insights Uptime | N/A | 99.95% | Week 1 |
| Static Content Cache Hit | N/A | >95% | Week 3 |

---

## Rollback Plans

Each phase has a rollback strategy:

**Phase 1 Rollback**: Remove security scanning steps (no infrastructure changes)  
**Phase 2 Rollback**: Revert to single-environment workflow  
**Phase 3 Rollback**: Remove SWA deployment, re-embed Blazor in API  
**Phase 4 Rollback**: Remove Docker caching configuration

---

## Resource Links

- [Azure Static Web Apps Docs](https://learn.microsoft.com/azure/static-web-apps/)
- [Application Insights Setup](https://learn.microsoft.com/azure/azure-monitor/app/app-insights-overview)
- [Trivy Security Scanner](https://github.com/aquasecurity/trivy)
- [GitHub Actions Best Practices](https://docs.github.com/en/actions/writing-workflows/workflow-syntax-for-github-actions)
- [Azure Bicep Documentation](https://learn.microsoft.com/azure/azure-resource-manager/bicep/)

---

**Last Updated**: 2026-05-12  
**Next Review Date**: 2026-06-12  
**Owner**: Cloud DevOps Team
