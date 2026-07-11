# Deploying AI Gateway to Azure

This is the deployment path from `ARCHITECTURE.md`'s "Deployment" section (Container Apps + managed Postgres +
managed Redis, containerized). It's structurally complete but **not verified against a real Azure subscription**
— nothing in this environment has cloud credentials. Treat `deploy/main.bicep` the same way CLAUDE.md treats
`AzureKeyVaultSecretStore`: reasonably confident it's correct, not proven live. If you deploy this for real and
hit something wrong, fix it and update this note.

## What's here

- `src/Api/Dockerfile`, `src/Management/Dockerfile`, `src/Dashboard/Dockerfile` — one image per deployable, all
  build and run locally (see "Local smoke test" below).
- `.github/workflows/cd.yml` — builds + pushes all three images to GHCR on every push to `main`
  (`ghcr.io/<owner>/ai-gateway-{api,management,dashboard}:<sha>` and `:latest`), then a `deploy` job that updates
  the three Container Apps to the new images — **skipped** until the repo variables below are set.
- `deploy/main.bicep` — provisions everything those Container Apps need to exist in the first place: a Container
  Apps environment (+ Log Analytics), Postgres Flexible Server, Azure Cache for Redis, a Key Vault (for
  `AzureKeyVaultSecretStore` — tenant BYOK credentials, a separate concern from the infra secrets below), and
  the three Container Apps themselves with their managed identities granted Key Vault Secrets Officer.

## One-time setup

1. **Provision the infrastructure** (creates the Container Apps that `cd.yml`'s deploy job later updates in
   place — you can't `containerapp update` an app that doesn't exist yet):
   ```bash
   az group create --name ai-gateway-prod --location eastus2
   az deployment group create \
     --resource-group ai-gateway-prod \
     --template-file deploy/main.bicep \
     --parameters namePrefix=aigw-prod \
                  apiImage=ghcr.io/<owner>/ai-gateway-api:latest \
                  managementImage=ghcr.io/<owner>/ai-gateway-management:latest \
                  dashboardImage=ghcr.io/<owner>/ai-gateway-dashboard:latest \
                  postgresAdminPassword=<generate one> \
                  authenticationMode=OidcAuthority \
                  oidcAuthority=<your IdP's authority URL>
   ```
   (Push to `main` once first so `:latest` images actually exist in GHCR, or point these params at any tag you
   already have.) `authenticationMode=StaticKey` also works for a first deploy before a real IdP is wired up —
   see `Core/Auth`'s doc comments for why that mode must never be left on past initial bring-up.

2. **Apply EF Core migrations** against the new Postgres server — this repo doesn't run migrations from inside
   the container image (`Api`/`Management`'s Dockerfiles publish the app only, not the `dotnet-ef` tool), so run
   them from a machine with the .NET SDK and network access to the new server (or a `dotnet ef` step you add to
   the deploy job):
   ```bash
   GATEWAY_DB_CONNECTION_STRING="<the connection string from the postgres deployment output>" \
     dotnet ef database update --project src/Core --startup-project src/Core
   ```

3. **Wire up GitHub → Azure OIDC federated credentials** (no client secret to leak or rotate) — create an app
   registration, grant it Contributor on the resource group, and add a federated credential scoped to this
   repo's `main` branch and the `production` GitHub environment (`cd.yml`'s deploy job declares
   `environment: production`). Then set, as repository secrets: `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`,
   `AZURE_SUBSCRIPTION_ID`. As repository (or `production` environment) variables: `AZURE_RESOURCE_GROUP`,
   `AZURE_CONTAINERAPP_API`, `AZURE_CONTAINERAPP_MANAGEMENT`, `AZURE_CONTAINERAPP_DASHBOARD` (the Container App
   *names*, i.e. `<namePrefix>-api` etc. from step 1). The deploy job in `cd.yml` is a no-op until
   `AZURE_RESOURCE_GROUP` is set — nothing fires by accident.

4. Push to `main`. `cd.yml`'s `build-and-push` job always runs; `deploy` picks up from there once the above is
   configured.

## Known simplifications (not yet hardened)

- Postgres firewall allows all Azure services (`0.0.0.0`–`0.0.0.0`, Azure's shorthand for that) rather than
  being locked to the Container Apps environment's outbound IPs via VNet integration — Container Apps doesn't
  have static outbound IPs without a NAT gateway, which is further infra this Bicep doesn't provision yet.
- `Management`'s ingress is external (publicly reachable), even though only `Dashboard`'s nginx proxy actually
  needs to reach it — internal-only ingress would shrink the attack surface further but wasn't worth the
  unverified complexity of internal-FQDN TLS wiring in IaC that can't be tested here. `Cors:AllowedOrigins`
  isn't needed either way, since `Dashboard`'s nginx proxies `/api/**` same-origin (see
  `src/Dashboard/nginx.conf.template`) — the same choice local dev makes via the Vite proxy.
- No custom domain / DNS — Container Apps' auto-generated `*.azurecontainerapps.io` FQDNs are used directly.
- `postgresAdminPassword` is passed as a plain secure parameter, not pulled from Key Vault — fine for first
  bring-up, worth revisiting alongside the VNet hardening above.

## Local smoke test (no Azure needed)

`docker-compose.full.yml` runs the actual images this pipeline builds — Api, Management, and Dashboard (behind
its nginx reverse proxy) — alongside the same Postgres/Redis `docker-compose.yml` already provides, as a local
check that the Dockerfiles/images are correct before trusting them to a real deploy:

```bash
docker compose -f docker-compose.yml -f docker-compose.full.yml up --build -d
dotnet ef database update --project src/Core --startup-project src/Core   # first run only
```

Then `http://localhost:5116` (Api), `http://localhost:5162` (Management), `http://localhost:8081` (Dashboard,
proxying `/api/**` to Management internally — same pattern as production). See `CLAUDE.md`'s "Commands" section
for the full local-dev walkthrough this mirrors.

**Verified working** (this environment has Docker but not Azure): all three images build; the full stack boots
and migrates; a real tenant was created via `Management`, a BYOK provider credential was set and correctly
decrypted back by `Api` from the same shared secrets volume (the cross-process Data Protection key-ring sharing
documented in `CLAUDE.md`'s `LocalDevSecretStore` note — confirmed it also holds up across separate *containers*,
not just separate local processes), and a real chat-completion request proxied through to `api.openai.com`
(rejected with a real `401`, since the smoke-test credential is fake — that in itself confirms the proxy path
works end-to-end). `docker-compose.full.yml`'s `Authentication__StaticKey__SigningKey` is a fixed, committed,
dev-only value for this smoke test only — never use `"StaticKey"` mode with a real deployment past initial
bring-up (see step 1 above).
