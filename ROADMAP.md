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
- [x] Generic, IdP-agnostic JWT bearer validation (`Core/Auth`: `IJwtAccessTokenValidator`, `Authority`/
  `Audience`-driven config, `"OidcAuthority"` mode fetches signing keys from the IdP's real discovery document —
  the same mechanism ASP.NET Core's own JWT bearer handler uses)
- [x] `Management`: JWT required on all `/tenants/**` routes (`AdminAuthenticationFilter`) — fully built and
  verified live (superadmin trust model: any valid token, no per-tenant admin restriction — open item)
- [~] `Api`: JWT accepted on the data-plane (`TenantAuthenticationFilter`), verified live with a locally-minted
  token resolving to a real tenant and its BYOK credential — **but** the legacy hashed-API-key scheme (Phase 3)
  is kept as a parallel, still-primary path, not replaced, because full OAuth2 client-credentials requires
  dynamic per-tenant client registration with a real IdP, which is blocked on having a real IdP account (see
  below)
- [ ] Managed IdP integration (Entra ID external tenants or Auth0) — **blocked**: no real IdP account available
  to build/verify OIDC discovery or dynamic client registration against. The validation *layer* is IdP-agnostic
  and ready (above); nothing has been tested against a real Entra ID/Auth0 tenant. Revisit when an account is
  available — see `ARCHITECTURE.md`'s "Open questions".

Scope note: given the IdP-account blocker, this phase deliberately built the reusable validation layer generic
across any OIDC-compliant IdP and applied it where it doesn't need one (Management's single-app admin login is
straightforward JWT validation; Api's dual-scheme is a bridge, not the end state). Local dev/test verification
used `"StaticKey"` mode (`LocalDevTokenIssuer`, `src/DevTools` CLI) — signing a token locally, never through an
HTTP endpoint on the running gateway, since "the gateway never issues tokens" is a hard architectural rule, not
just a production concern.

Verified live end-to-end: booted `Api` + `Management` against real Postgres; confirmed `/healthz` stays open
while `/tenants/**` now 401s without a token; created a tenant and set a provider credential using an admin JWT;
minted a second, tenant-scoped JWT and used it (not an API key) to call `/v1/chat/completions`, confirming it
resolved the correct tenant and BYOK credential (reached the real OpenAI host, got an OpenAI-shaped error for
the intentionally-fake key); confirmed the legacy API-key path still works unchanged; confirmed a JWT for a
nonexistent tenant is rejected with `401`.

## Phase 5 — Streaming
- [x] SSE passthrough (`stream: true`) for both providers — OpenAI is a byte pass-through (public contract
  already matches OpenAI's SSE shape); Anthropic gets a real frame-by-frame translation
  (`AnthropicStreamTranslator`, pure/stateful, unit-tested independent of any HTTP mocking)
- [x] Token usage finalized from completed stream — captured and logged (`ILogger`, no metrics/rate-limit
  consumer yet; that's Phases 6/7, this just wires the hook)
- [x] Correctness fix found during design (not live verification this time — caught while implementing, before
  it ever ran): a provider rejecting a request before sending any stream data (bad credentials, etc.) must not
  be forwarded as if it were a successful SSE stream. Real providers return a normal non-streaming JSON error
  with a real status code for this, not a mid-stream event — so the gateway now does the same
  (`IStreamResponseWriter`, lets a provider client switch the outer response to `application/json` + the real
  status code, but only works before the first byte is written to the stream body). Verified live against real
  `api.openai.com` and `api.anthropic.com` with intentionally-invalid keys: both return `401` with
  `Content-Type: application/json`, and the Anthropic error is correctly translated into the OpenAI-shaped
  `{"error": {...}}` form.
- [x] Test coverage: 26 new tests (83 total) — `AnthropicStreamTranslator` pure-logic tests, both provider
  clients' streaming I/O (canned SSE via `FakeHttpMessageHandler`, including the error-before-streaming path),
  and `Api.Tests` integration coverage through the real `Results.Stream`/`HttpResponse` pipeline (not just a
  fake) for both the happy path and the error-switch path, since that's exactly where the trickiest assumption
  in this phase lives (can response status/content-type still change from inside the stream callback before the
  first write — confirmed yes, live and in tests)

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
