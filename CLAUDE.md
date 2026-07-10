# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project purpose

AI Gateway is intended to become a multi-tenant, cloud-hosted SaaS product that routes companies' AI usage to
inference providers (Anthropic, OpenAI, etc.), with OAuth2-secured access, token-usage monitoring/metrics,
token-usage-based rate limiting, and an OpenAI-compatible REST surface so existing OpenAI SDK clients can point
at this gateway as a drop-in replacement.

The full target architecture — repo/project layout, multi-tenancy model, auth design, provider integration,
rate limiting, observability, and deployment — is planned out in **`ARCHITECTURE.md`**. The phased build order is
in **`ROADMAP.md`** — check it before starting new work so you're building the current phase, not a later one
out of order. Read both before making structural decisions (new projects, auth flow, data model, etc.).

## Current state

Phases 0–3 are done (solution scaffolding, core domain + persistence, walking-skeleton proxy, multi-tenancy +
BYOK credentials) — see `ROADMAP.md` for what's next. There is still no OAuth2/managed-IdP auth (API keys are
the only auth today), no rate limiting, and no streaming. Check `ROADMAP.md` before starting new work so you're
building the current phase, not a later one out of order, and update it as phases complete. Update
`ARCHITECTURE.md` too if you make a decision that resolves one of its "Open questions".

## Solution structure

`src/Gateway.slnx` (new `.slnx` format) references all projects below. All .NET projects target `net10.0` with
nullable reference types and implicit usings enabled.

- `src/Api/` — ASP.NET Core Web API (`Microsoft.NET.Sdk.Web`), the data-plane proxy. References `Core`, wired
  up to Postgres via `AddGatewayPersistence`, providers via `AddProviderClients`, secrets via
  `AddGatewaySecrets`, and `AddApiKeyAuthentication`. Endpoints:
  - `GET /.well-known/ai-routing-configuration` — stub, unimplemented.
  - `POST /v1/chat/completions` (`Endpoints/ChatCompletionsEndpoint.cs`) — requires
    `Authorization: Bearer <tenant API key>` (enforced by `.AddEndpointFilter<ApiKeyAuthenticationFilter>()` on
    the route, see `Authentication/ApiKeyAuthenticationFilter.cs`). Resolves the tenant from that key, picks a
    provider from the model name (`Core.Providers.ProviderRouting` — currently a `"claude*" → anthropic, else →
    openai` heuristic, not real per-tenant config; see its doc comment), looks up that tenant's BYOK credential
    for the resolved provider via `ISecretStore`, and proxies through `IProviderClientRegistry`. Non-streaming
    only (`stream:true` returns `400`).
- `src/Management/` — ASP.NET Core Web API (`Microsoft.NET.Sdk.Web`), the control-plane API. References `Core`,
  wired the same way as `Api` (persistence, providers, secrets) minus API-key auth. Every request runs
  `TenantScope.Unscoped` (set by middleware in `Program.cs`) — Management is the trusted control plane and
  operates across tenants by design; it has no per-request auth yet (that's Phase 4 — OIDC/SSO for admin users).
  Endpoints (all under `Endpoints/`):
  - `POST /tenants`, `GET /tenants/{tenantId}` — tenant CRUD (create/read only so far).
  - `POST /tenants/{tenantId}/api-keys` — issues a key; the plaintext is only ever in this one response
    (`ApiKeyGenerator.GenerateSecret()`/`.Hash()` in `Core/Security`) — the DB stores only the hash.
  - `DELETE /tenants/{tenantId}/api-keys/{apiKeyId}` — revokes (soft: sets `RevokedAtUtc`).
  - `PUT /tenants/{tenantId}/providers/{providerName}` — stores a tenant's BYOK credential for that provider via
    `ISecretStore`, keyed by `ProviderCredentialSecretName.For(tenantId, providerName)`.
- `src/Core/` — class library for shared domain code used by both `Api` and `Management`:
  - `Entities/` — `Tenant`, `ApiKey`.
  - `Persistence/` — `GatewayDbContext` (EF Core + Npgsql), `GatewayDbContextFactory` (design-time factory for
    `dotnet ef` tooling), `ServiceCollectionExtensions.AddGatewayPersistence` (DI registration), and
    `Migrations/` (EF Core migrations — commit these, don't hand-edit generated migration files).
  - `Tenancy/` — the multi-tenancy scoping mechanism: `TenantScope`/`TenantScopeMode` (`Blocked`,
    `SingleTenant`, `Unscoped`) and `ICurrentTenantAccessor` (default impl: `AsyncLocalCurrentTenantAccessor`).
    `GatewayDbContext` applies a global query filter on tenant-owned entities (currently `ApiKey`) keyed off
    the current scope. **The default/unset scope is `Blocked`, not `Unscoped`** — this is deliberate fail-closed
    behavior per `ARCHITECTURE.md`'s multi-tenancy section; a caller must explicitly set a tenant or explicitly
    opt into `Unscoped` (trusted admin paths only) to see any rows. When adding a new tenant-owned entity, give
    it a `TenantId` column and add the same `HasQueryFilter` pattern in `GatewayDbContext.OnModelCreating`.
  - `Security/` — `ApiKeyGenerator` (generate/hash tenant API keys — SHA-256 is intentional here, not a bug:
    the input is a 32-byte random secret with far more entropy than a password, so slow password-hashing
    algorithms aren't needed) and `ApiKeyAuthenticator`/`IApiKeyAuthenticator` (looks up an `ApiKey` by hash
    using `IgnoreQueryFilters()` — deliberately bypasses the tenant scope for this one bootstrap query, since
    resolving the tenant *is* the point, rather than flipping the ambient scope to `Unscoped` and back).
  - `Secrets/` — `ISecretStore` (`SetSecretAsync`/`GetSecretAsync`/`DeleteSecretAsync`) with two implementations,
    selected by the `Secrets:Provider` config value via `AddGatewaySecrets`:
    - `AzureKeyVaultSecretStore` ("AzureKeyVault", requires `Secrets:AzureKeyVault:VaultUri`) — the production
      path. Not exercised by any test or live run here (no real Key Vault instance available) — reasonably
      confident it's structurally correct, but it hasn't been verified end-to-end the way the local path has.
    - `LocalDevSecretStore` ("LocalDev") — **dev-only, never production** (see its doc comment). Encrypts with
      ASP.NET Core Data Protection and stores in a JSON file (`Secrets:LocalDev:FilePath`, default
      `.local/secrets.dev.json`, gitignored). `AddGatewaySecrets` explicitly configures a *shared, persisted*
      Data Protection key ring (fixed application name + a directory next to the secrets file) — **do not**
      let this go back to the ASP.NET Core default. `Api` and `Management` are separate processes, and the
      default Data Protection setup gives each process its own isolated key ring; a secret `Management` writes
      would be undecryptable by `Api`, and this fails at request time with a `CryptographicException`, not at
      startup. This was a real bug caught during live end-to-end verification — see the comment above
      `PersistKeysToFileSystem` in `Core/Secrets/ServiceCollectionExtensions.cs`.
  - `Providers/` — `IProviderClient` (request/response passed through as `JsonObject`, not fully typed — see
    the doc comment on the interface for why), `IProviderClientRegistry`/`ProviderClientRegistry` (resolves a
    client by `ProviderName` from all registered `IProviderClient`s), `ProviderRouting` (the model-name-prefix
    heuristic mentioned above), `OpenAiProviderClient`, and `AddProviderClients`/`AddOpenAiProviderClient`/
    `AddAnthropicProviderClient` (DI registration). No credential is baked into any client at construction time
    — `CreateChatCompletionAsync(request, apiKey, cancellationToken)` takes the tenant's BYOK key per call,
    since one client instance is shared across every tenant using that provider.
    **Registration gotcha, already hit once:** each provider's `AddHttpClient<...>()` call must key the typed
    client by its own *concrete* type (`OpenAiProviderClient`, `AnthropicProviderClient`), never by the shared
    `IProviderClient` interface. `AddHttpClient<TClient, TImplementation>` names the underlying named
    `HttpClient` after `TClient`, and `IHttpClientFactory` applies every `ConfigureClient` delegate registered
    for a given name to *any* client built under it — so two providers both registered as
    `AddHttpClient<IProviderClient, TImpl>` would silently share one named client, and whichever was registered
    last would win the `BaseAddress` for both. Caught live: an OpenAI-routed request came back with an
    Anthropic-shaped error because `OpenAiProviderClient` actually had `BaseAddress` pointed at
    `api.anthropic.com`. Regression test:
    `Core.Tests/Providers/ServiceCollectionExtensionsTests.cs`. If you add a third provider, follow the existing
    pattern (own concrete type + a separate `services.AddTransient<IProviderClient>(sp => sp.GetRequiredService<...>())`
    bridge), not the interface-keyed one.
    - `Providers/Anthropic/` — `AnthropicProviderClient` and `AnthropicChatTranslator` (pure functions
      translating the gateway's OpenAI-shaped public contract to/from Anthropic's native Messages API — moves
      `system` messages out of the array, defaults `max_tokens` when the caller omits it, maps `stop_reason` to
      OpenAI's `finish_reason` vocabulary). Known limitations documented on the translator's methods: string
      message content only (no multimodal), and only text content blocks are read back from the response.
- `src/Dashboard/` — React + TypeScript SPA (Vite), for tenant self-service. Currently the unmodified Vite
  starter template, not wired to any API yet.
- `src/Core.Tests/` — covers the tenant query-filter behavior, both provider clients (fake `HttpMessageHandler`,
  no real network calls), the Anthropic translator (pure-function unit tests), the provider-registration
  regression above, `ApiKeyGenerator`, and `LocalDevSecretStore` (round-trip + cross-instance decryption using
  `EphemeralDataProtectionProvider`).
- `src/Api.Tests/` — `ChatCompletionsEndpointTests.cs`, a `WebApplicationFactory<Program>` integration test.
  Swaps in the EF Core InMemory provider for `GatewayDbContext` (see the `RemoveAll<DbContextOptions<...>>` +
  `RemoveAll<IDbContextOptionsConfiguration<...>>` + re-`AddDbContext` pattern — both removals are needed, or
  EF Core throws "two database providers registered" at runtime), a stub `IProviderClient`, and a stub
  `ISecretStore`, and seeds a real tenant + API key through the `GatewayDbContext` directly so tests authenticate
  the same way a real client would. `Program.cs` has `public partial class Program;` at the bottom specifically
  to make it accessible to `WebApplicationFactory<Program>` — keep that if you touch `Program.cs`.
- `src/Management.Tests/` — `ManagementApiFactory` (shared `WebApplicationFactory<Program>`, same InMemory-DB
  swap pattern as `Api.Tests`, plus an in-memory `ISecretStore`) and endpoint tests per resource
  (`TenantsEndpointTests`, `ApiKeysEndpointTests`, `ProviderCredentialsEndpointTests`).
  **Gotcha already hit once:** generate the InMemory database name *once* (e.g. a field, computed outside the
  `AddDbContext` configure lambda) and reuse it — generating it inline inside the lambda (`UseInMemoryDatabase(Guid.NewGuid().ToString())`)
  means every time EF Core re-invokes that delegate you silently get a fresh, empty database, so data written by
  one request "disappears" for the next. This looked like a 404-after-create bug before the cause was found.

Endpoint handlers that take `HttpRequest`/services as minimal-API parameters have those resolved by DI **before**
the handler body runs — don't add a service as a bound parameter if constructing it can fail in ways you want
validated request data to short-circuit first (see `ChatCompletionsEndpoint.HandleAsync`, which resolves
`IProviderClient`/`ISecretStore`/`ICurrentTenantAccessor` lazily via `HttpContext.RequestServices` after
validation, specifically to avoid this).

Note: `Microsoft.EntityFrameworkCore.Relational` is pinned explicitly in `Core/Core.csproj` (not just pulled in
transitively via Npgsql/EFCore.Design) to avoid an assembly version conflict between what Npgsql's package floor
resolves and what EFCore.Design expects. If you add another EF-related package and see `MSB3277` conflict
warnings, pin the conflicting package version in `Core.csproj` rather than in downstream projects.

## Commands

Run .NET commands from `src/` (where `Gateway.slnx` lives), or point `dotnet` at the `.slnx`/`.csproj` explicitly.

```bash
# restore / build / test the whole solution
dotnet restore src/Gateway.slnx
dotnet build src/Gateway.slnx
dotnet test src/Gateway.slnx

# run a single test
dotnet test src/Gateway.slnx --filter FullyQualifiedName~SomeTestClass.SomeTestMethod

# run the data-plane API (http: localhost:5116, https: localhost:7251)
dotnet run --project src/Api

# run the control-plane API (http: localhost:5162, https: localhost:7071)
dotnet run --project src/Management
```

```bash
# Dashboard (run from src/Dashboard)
npm install
npm run dev       # local dev server
npm run build     # production build (tsc -b && vite build)
```

### Local Postgres + EF Core migrations

`docker-compose.yml` (repo root) runs a local Postgres for development, matching the connection string in
`src/Api/appsettings.Development.json` and `src/Management/appsettings.Development.json`
(`Host=localhost;Port=5432;Database=ai_gateway;Username=ai_gateway;Password=ai_gateway` — dev-only credentials).

```bash
docker compose up -d          # start local Postgres (from repo root)

# EF Core tooling (dotnet-ef is a local tool — see .config/dotnet-tools.json; run `dotnet tool restore` once)
dotnet ef migrations add <Name> --project src/Core --startup-project src/Core --output-dir Persistence/Migrations
dotnet ef database update --project src/Core --startup-project src/Core
```

`GatewayDbContextFactory` (used by the `dotnet ef` commands above) reads the connection string from the
`GATEWAY_DB_CONNECTION_STRING` env var, falling back to the same local dev default if unset.

CI (`.github/workflows/ci.yml`) runs `dotnet build`/`dotnet test` on the solution and `npm run build` on the
Dashboard for every PR. It does not currently run against a real Postgres instance or apply migrations — that's
an open gap, not a deliberate decision; revisit if/when integration tests need a real database in CI.

### Exercising the tenant onboarding → BYOK → proxy flow locally

With Postgres running (above) and both `Api` and `Management` started:

```bash
# create a tenant, issue an API key, set a provider credential
curl -X POST http://localhost:5299/tenants -H "Content-Type: application/json" -d '{"name":"Acme"}'
curl -X POST http://localhost:5299/tenants/<tenantId>/api-keys -H "Content-Type: application/json" -d '{"name":"prod"}'
curl -X PUT http://localhost:5299/tenants/<tenantId>/providers/openai -H "Content-Type: application/json" -d '{"apiKey":"sk-..."}'

# call the data-plane with the issued key
curl -X POST http://localhost:5298/v1/chat/completions \
  -H "Authorization: Bearer <the key from api-keys response>" -H "Content-Type: application/json" \
  -d '{"model":"gpt-4o-mini","messages":[{"role":"user","content":"hi"}]}'
```

Both `Api` and `Management` default to `Secrets:Provider = "LocalDev"` in `appsettings.Development.json`, storing
encrypted credentials in `.local/secrets.dev.json` (gitignored) at the repo root — see the `LocalDevSecretStore`
note above for why the Data Protection key ring configuration there matters. If the two processes ever seem to
disagree about a stored credential (`CryptographicException` on `GetSecretAsync`), it usually means
`.local/secrets.dev.json` predates a change to that key-ring setup; deleting `.local/` and re-onboarding the
tenant is the fix, not debugging decryption further.
