# Implementation Roadmap

Phased build-out for the architecture in `ARCHITECTURE.md`. Phases are ordered so each one produces something
runnable/testable before the next adds complexity, rather than building every subsystem in parallel. Don't start
a phase's work until prior phases are functionally complete — each one assumes the previous exists.

Status legend: `[ ]` not started, `[~]` in progress, `[x]` done. Update this file as phases progress.

## Phase 0 — Solution scaffolding
Get the multi-project structure in place and building, with no business logic yet.
- [x] `src/Core` class library (shared domain, empty for now)
- [x] `src/Management` ASP.NET Core Web API project (empty for now, `GET /healthz` stub)
- [x] `src/Dashboard` React + TypeScript SPA (default Vite template, not wired to any API yet)
- [x] Test projects: `src/Core.Tests`, `src/Api.Tests`, `src/Management.Tests` (xUnit, no tests yet)
- [x] All projects registered in `Gateway.slnx`
- [x] GitHub Actions CI: restore/build/test the .NET solution and build the Dashboard on PRs

## Phase 1 — Core domain + persistence
No proxying, no auth yet — just the data model everything else depends on.
- [x] Postgres integration (EF Core `GatewayDbContext` in `src/Core/Persistence`, Npgsql provider)
- [x] `Tenant` and `ApiKey` entities, initial migration (`InitialCreate`, verified applying against real Postgres)
- [x] Global query filters for `tenant_id` scoping (`Core/Tenancy`: fail-closed `TenantScope`/
  `ICurrentTenantAccessor`, covered by tests in `Core.Tests/Persistence/TenantScopeQueryFilterTests.cs`)
- [x] Local dev Postgres via Docker Compose (`docker-compose.yml` at repo root)
- [x] `Api` and `Management` wired to persistence via `AddGatewayPersistence`, verified booting against the
  local Postgres

Not done, deferred to later phases: CI does not yet run against a real Postgres or apply migrations (no
integration tests exist yet to need it); production connection string / secrets handling is still the
`ConnectionStrings:Gateway` dev default — real secrets management is Phase 3+ (Key Vault) territory.

## Phase 2 — Walking-skeleton proxy (single provider, non-streaming)
Prove the request path end-to-end before adding tenancy/auth/streaming on top of it.
- [x] `Api` proxies `POST /v1/chat/completions` to OpenAI, non-streaming, using one dev-config API key
  (not yet tenant-scoped or BYOK) — `Endpoints/ChatCompletionsEndpoint.cs`
- [x] Provider client abstraction (`IProviderClient`) in `src/Core`, OpenAI implementation
  (`Core/Providers/IProviderClient.cs`, `OpenAiProviderClient.cs`)
- [x] Integration test hitting the endpoint against a stubbed provider
  (`Api.Tests/ChatCompletionsEndpointTests.cs`, `WebApplicationFactory` + stub `IProviderClient`), plus a unit
  test of `OpenAiProviderClient` itself against a fake `HttpMessageHandler`
  (`Core.Tests/Providers/OpenAiProviderClientTests.cs`)
- [x] Verified live: booted `Api`, confirmed malformed-JSON / missing-`model` / `stream:true` all return `400`
  with a clear error body, and that a valid request without a configured OpenAI key fails clearly (`500` with
  an `OptionsValidationException`) rather than hanging or silently misbehaving

Found and fixed during verification: minimal-API endpoint parameters are resolved by DI *before* the handler
body runs, so having `IProviderClient` as a bound parameter meant provider misconfiguration (`OptionsValidationException`
from the missing API key) fired for *every* request — including ones that should have failed request validation
first (missing `model`, malformed JSON, `stream:true`). Fixed by resolving `IProviderClient` lazily via
`HttpContext.RequestServices` after validation passes. Worth remembering if more endpoints take service
parameters that can throw during construction/configuration.

## Phase 3 — Multi-tenancy + BYOK credentials
- [x] `Management` API: tenant CRUD (`POST`/`GET /tenants`), API key issuance/revocation
  (`POST`/`DELETE /tenants/{id}/api-keys`), provider credential storage (`PUT /tenants/{id}/providers/{name}`)
- [x] Provider credential storage via `ISecretStore`: `AzureKeyVaultSecretStore` (production path, not yet
  exercised against a real vault) and `LocalDevSecretStore` (dev-only, Data-Protection-encrypted file — see
  `CLAUDE.md`), selected by `Secrets:Provider` config
- [x] `Api` resolves tenant from the incoming API key (`ApiKeyAuthenticationFilter` → `IApiKeyAuthenticator`,
  hash-lookup with `IgnoreQueryFilters()`) and looks up that tenant's BYOK provider credential rather than using
  dev config
- [x] Anthropic provider client added alongside OpenAI, with request/response translation
  (`AnthropicChatTranslator`) so both are reachable through the same OpenAI-shaped public endpoint
- [x] Provider selection: `ProviderRouting` (model-name-prefix heuristic — documented as a stopgap, not final
  design; see its doc comment and the "Open questions" in `ARCHITECTURE.md`)
- [x] Test coverage: 51 tests total across the solution — translator unit tests, both provider clients against
  fake HTTP handlers, tenant query-filter + API-key-auth behavior, `LocalDevSecretStore` round-trip/cross-instance
  decryption, and full `WebApplicationFactory` integration tests for both `Api`'s chat-completions endpoint and
  all three `Management` endpoints (EF Core InMemory provider, no real Postgres needed to run the suite)
- [x] Verified live end-to-end against real Postgres: created a tenant, issued an API key, set both an OpenAI
  and an Anthropic credential via `Management`, then called `Api`'s `/v1/chat/completions` with the issued key
  for both a `gpt-*` and a `claude-*` model and confirmed each request reached the *correct* provider host with
  the *correct* tenant credential (verified via provider-shaped error messages for the intentionally-fake keys)

Two real bugs found and fixed during live verification (not caught by the test suite beforehand — both were
cross-process/cross-request wiring issues that unit and even most integration tests wouldn't surface):

1. **Cross-process Data Protection key ring mismatch.** `Api` and `Management` are separate processes; ASP.NET
   Core's default Data Protection setup gives each its own isolated key ring, so a secret `Management` encrypted
   couldn't be decrypted by `Api` (`CryptographicException` at request time, not at startup). Fixed by
   explicitly configuring a shared, persisted key ring (fixed application name + a directory on disk) in
   `AddGatewaySecrets`'s `"LocalDev"` branch.
2. **`AddHttpClient<IProviderClient, TImpl>` collision across two providers.** Typed-client registration names
   the underlying named `HttpClient` after `TClient`, not `TImplementation`. Registering both OpenAI's and
   Anthropic's clients against the shared `IProviderClient` interface meant both `BaseAddress`-configuring
   delegates applied to the *same* named client, and the one registered last (Anthropic) silently won for both
   — an OpenAI-routed request was actually being sent to `api.anthropic.com`. Caught because the response body
   was Anthropic-shaped for a `gpt-4o-mini` request. Fixed by keying each provider's typed client by its own
   concrete type and bridging to `IProviderClient` separately; regression test added
   (`Core.Tests/Providers/ServiceCollectionExtensionsTests.cs`).

A third, lower-stakes bug was caught while writing `Management.Tests`: generating the EF Core InMemory database
name *inside* the `AddDbContext` configure lambda (`UseInMemoryDatabase(Guid.NewGuid().ToString())`) rather than
capturing it once beforehand meant every re-invocation of that delegate got a fresh, empty database — data
written in one request "disappeared" for the next. Looked like a 404-after-create logic bug before the cause
was found; see the `ManagementApiFactory`/`ChatCompletionsEndpointTests` note in `CLAUDE.md`.

## Phase 4 — AuthN / AuthZ
- [ ] Managed IdP integration (Entra ID external tenants or Auth0)
- [ ] `Api`: OAuth2 client-credentials validation on the data-plane
- [ ] `Management`: OIDC/SSO login for tenant admin users

## Phase 5 — Streaming
- [ ] SSE passthrough (`stream: true`) for both providers
- [ ] Token usage finalized from completed stream for downstream metrics/rate-limit accounting

## Phase 6 — Rate limiting
- [ ] Redis-backed sliding-window counters
- [ ] Enforcement per tenant and per API key
- [ ] Quota check before proxying, usage recorded after response/stream completes

## Phase 7 — Observability & usage data
- [ ] OpenTelemetry instrumentation (traces + metrics), OTLP export
- [ ] Usage-event persistence (metadata only — no prompt/completion payloads)
- [ ] `Management` API endpoints for querying usage

## Phase 8 — Dashboard
- [ ] `Dashboard` SPA wired to `Management` API: tenant onboarding, API key management
- [ ] Usage charts from Phase 7 data

## Phase 9 — Quota alerting
- [ ] Threshold-based notifications (webhook/email — delivery mechanism still an open question in
  `ARCHITECTURE.md`)

## Phase 10 — Deployment hardening & follow-ons
- [ ] Azure Container Apps deployment, managed Postgres + Redis
- [ ] CD pipeline
- [ ] Revisit open questions from `ARCHITECTURE.md`: billing/invoicing, pooled provider keys, per-tenant DB
  isolation for compliance customers, additional providers (Azure OpenAI, etc.)
