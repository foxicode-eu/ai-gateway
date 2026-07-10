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
- [ ] `Api` proxies `POST /v1/chat/completions` to OpenAI, non-streaming, using one dev-config API key
  (not yet tenant-scoped or BYOK)
- [ ] Provider client abstraction (`IProviderClient`) in `src/Core`, OpenAI implementation
- [ ] Basic integration test hitting the endpoint against a stubbed provider

## Phase 3 — Multi-tenancy + BYOK credentials
- [ ] `Management` API: tenant CRUD, API key issuance
- [ ] Provider credential storage via Azure Key Vault, looked up per tenant
- [ ] `Api` resolves tenant + provider key from the incoming API key rather than dev config
- [ ] Anthropic provider client added alongside OpenAI

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
