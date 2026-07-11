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
- [x] Redis-backed sliding-window counters (`Core/RateLimiting/TokenRateLimiter` — the standard weighted
  current+previous-fixed-window approximation, O(1) store ops; `RedisRateLimitStore` production /
  `InMemoryRateLimitStore` local-dev-and-tests, same provider-swap pattern as `Secrets`)
- [x] Enforcement per tenant and per API key (`Tenant.TokenQuotaPerWindow` / `ApiKey.TokenQuotaPerWindow`, both
  nullable = unlimited, settable at creation and via new `Management` `PATCH` endpoints; JWT-authenticated
  requests are tenant-only since that credential model has no notion of "which key")
- [x] Quota check before proxying, usage recorded after response/stream completes (`Api/RateLimiting/RateLimitGate`)
- [x] Test coverage: 19 new tests (102 total) — `TokenRateLimiter` algorithm correctness (deterministic fake
  clock, including exact-boundary and mid-window-decay cases), full `Api.Tests` integration coverage of the
  blocking/passthrough/per-key-vs-per-tenant/JWT-exemption behavior, and `Management.Tests` coverage of the new
  quota fields and `PATCH` endpoints
- [x] Verified live against **real Redis** (not just the in-memory test double, which is all the automated
  suite exercises) — added `redis:7-alpine` to `docker-compose.yml`. Booted `Api` against it, manually seeded a
  Redis counter key in the exact format `TokenRateLimiter` uses, past a tenant's configured quota, and confirmed
  a live request was correctly blocked with `429`; confirmed a tenant with no quota configured is not blocked
  and reaches the real provider host. Full record→check consumption tracking is covered in `Api.Tests` against
  `InMemoryRateLimitStore` only — doing the same live wasn't practical without a real provider API key, since a
  failed provider call (the only kind possible with the fake keys available here) never produces token usage to
  record in the first place.

## Phase 7 — Observability & usage data
- [x] OpenTelemetry instrumentation (traces + metrics), OTLP export (`Core/Observability` —
  `AddGatewayObservability`, ASP.NET Core + HttpClient auto-instrumentation on both `Api`/`Management`, plus a
  custom `AiGateway` `ActivitySource`/`Meter` on `Api` for `gateway.requests`/`gateway.request.duration`/
  `gateway.tokens`, tagged tenant/provider/model/status. Exporter swappable `"Otlp"` (production) /
  `"Console"` (local dev, same provider-swap pattern as `Secrets`/`RateLimiting`) via `Observability:Exporter`)
- [x] Usage-event persistence, metadata only (`Core.Entities.UsageEvent` + `Api/Observability/UsageEventRecorder`
  — tenant, API key if legacy-authenticated, provider, model, streamed flag, status code, prompt/completion
  tokens, latency; never prompt/completion content)
- [x] `Management` API endpoints for querying usage (`GET /tenants/{id}/usage?sinceHours=` — aggregate
  totals + per-provider breakdown over a configurable window)
- [x] Test coverage: 9 new tests (111 total) — `Api.Tests` covers usage-event correctness for successful,
  streaming, rate-limited, and no-credential requests; `Management.Tests` covers the usage endpoint's
  aggregation/grouping/time-window math and its `404`/`401` cases
- [x] Verified live end-to-end: booted `Api` with `Observability:Exporter=Console`, made a real request, and
  confirmed the console output showed the custom `chat.completion` span correctly nested under the ASP.NET Core
  request span with the OpenAI `HttpClient` call nested under *that*, plus `gateway.requests`/
  `gateway.request.duration` metrics emitted with the expected tags — not just that instrumentation compiled,
  that it actually produces the nested-span shape you'd want in a real trace viewer. Also confirmed a
  `usage_events` row landed in Postgres with the right data, and that `Management`'s usage endpoint correctly
  reflected it (1 request, 1 error, grouped under `"openai"`).

Not built (explicitly out of scope for this phase, tracked in `ARCHITECTURE.md`): per-tenant usage *dashboards*
(no `Dashboard` project exists yet — this phase built the API a future one would call) and quota-threshold
alerting (that's Phase 9).

## Phase 8 — Dashboard
- [x] Session-based auth infrastructure for `Management` + `Dashboard`, chosen (per explicit direction, ahead of
  the rest of this phase's design) specifically to reduce token-leakage exposure in the browser versus storing a
  JWT client-side: `Core/Sessions` (`ISessionStore`, `RedisSessionStore`/`InMemorySessionStore` — same
  provider-swap pattern as `Secrets`/`RateLimiting`), `Management/Authentication/SessionCookies`
  (HttpOnly/SameSite=Lax opaque session cookie, server-side revocable), `POST /auth/login|logout`,
  `GET /auth/session` (`Endpoints/AuthEndpoint.cs`). `AdminAuthenticationFilter` now accepts either a session
  cookie or the existing bearer JWT — deliberate dual scheme, Dashboard uses the former, `curl`/`DevTools`/
  automation keep using the latter. The credential exchanged at `/auth/login` is still a `DevTools`-minted dev
  JWT for now, but the exchange is structured so a real IdP login flow can be swapped in later without touching
  `SessionCookies` or the store.
- [x] Three new `GET` list endpoints the Dashboard needed and `Management` didn't have yet: `GET /tenants`,
  `GET /tenants/{id}/api-keys` (never the hash/plaintext), `GET /tenants/{id}/providers` (configured-status only,
  never the credential value)
- [x] `Dashboard` SPA wired to `Management` API: tenant onboarding, API key management. Stack chosen by explicit
  user direction: TanStack Router (code-based routes) + TanStack Query + shadcn/ui + Tailwind CSS v4 + Recharts.
  Vite dev-server proxy makes Dashboard↔Management same-origin locally, sidestepping cross-origin cookie
  complexity. Pages: login, tenant list (+ create-tenant dialog), tenant detail (quota, API keys, provider
  credentials, usage chart cards).
- [x] Usage charts from Phase 7 data (`UsageCard.tsx`, Recharts bar chart of the `Management` usage endpoint's
  per-provider breakdown)
- [x] Test coverage: 14 new backend tests (125 total) — `Core.Tests` covers `InMemorySessionStore`
  (round-trip/unknown/removal/expiry), `Management.Tests` adds `AuthEndpointTests` (full session lifecycle:
  login sets a working cookie, invalid token rejected with no cookie, logout invalidates, `GET /auth/session`
  reflects state, bearer-JWT path still works without a cookie) and a list-endpoint test per new `GET` route.
  `Dashboard` has no automated test suite yet (open item in `ARCHITECTURE.md`) — verified via `npm run build`/
  `npm run lint` plus live browser driving instead.
- [x] Verified live: backend session flow against real Redis via `docker compose` (`Set-Cookie` on login,
  `redis-cli KEYS 'session:*'` shows the real key, request without cookie → `401`, with cookie → succeeds,
  logout → `204` and the Redis key is gone). Dashboard verified with a real headless-browser session (Playwright,
  `chromium-cli` unavailable in this environment) driving the actual UI: login → tenant list → create tenant →
  tenant detail → issue API key → set a provider credential → usage chart renders with real data from a prior
  phase's verification run. This surfaced a real bug no unit/integration test caught: `LoginPage`'s
  `queryClient.setQueryData` write on login success doesn't synchronously flush a re-render before an immediately
  following `navigate()` call, so the newly-mounted `/` route's `RequireAuth` briefly saw stale
  `isAuthenticated: false` and bounced back to `/login` even though login had actually succeeded. Fixed by
  replacing the imperative post-login `navigate()` with a `useEffect` that navigates once `isAuthenticated`
  reactively flips true — see `CLAUDE.md`'s "Sessions" section for detail.

Not built (explicitly out of scope for this phase, tracked in `ARCHITECTURE.md`): an automated Dashboard test
suite, and the production (non-local-dev) Dashboard↔Management CORS/cookie topology, which depends on hosting
choices not yet made.

## Phase 9 — Quota alerting
- [x] Delivery mechanism and trigger design decided by explicit direction ahead of implementation: webhook-only
  (email deferred, not ruled out), per-tenant configurable threshold percentages (not a fixed 80/100), checked
  inline with the existing rate-limit-recording path on `Api` rather than a new background job.
- [x] Schema: `Tenant.AlertWebhookUrl` (null = disabled) + `Tenant.AlertThresholdPercentages` (`int[]`,
  Postgres native array column via a new `AddQuotaAlerting` EF Core migration), both settable via
  `Management`'s `PATCH /tenants/{id}` (validated: absolute `http(s)` URL, thresholds `1`-`100`) and the
  Dashboard's `QuotaCard` (extended into "Token quota & alerts", one form/one save so a plain quota edit can't
  accidentally clear alerting under the existing "always applied as given" `PATCH` semantics).
- [x] `Core/Alerting` (`IQuotaAlertSender`/`WebhookQuotaAlertSender` — POSTs a JSON payload, no `BaseAddress`
  since webhook URLs are per-tenant/arbitrary) + `Api/Alerting/QuotaAlertGate` (the decision logic: re-checks
  the tenant's usage via the *same* rate-limiter key `RateLimitGate` enforces admission with — extracted to
  `Core.RateLimiting.RateLimitKeys` so the two call sites can't drift on the key format — fires the *highest*
  crossed threshold if any, tracks "already alerted this window" in the same rate-limit store under an
  `alert:tenant:{id}:{windowIndex}` key via an increment-only "raise to at least X" trick, since
  `IRateLimitStore` has no direct "set"). Wired into `ChatCompletionsEndpoint`'s `FinishAsync` right after
  `RateLimitGate.RecordUsageAsync`, on every exit path. A webhook delivery failure is logged and swallowed —
  never breaks the chat-completion request that triggered the check.
- [x] Test coverage: 11 new backend tests (136 total) — `Core.Tests` covers `WebhookQuotaAlertSender` (fake
  `HttpMessageHandler`, payload shape + non-2xx-throws); `Api.Tests` covers the full alerting decision matrix
  (fires on crossing, doesn't fire below threshold, doesn't re-fire the same threshold twice in one window,
  fires again for a higher threshold, never fires with no webhook configured) via a stub `IQuotaAlertSender`;
  `Management.Tests` covers `PATCH` validation and round-tripping of the new tenant fields, including clearing
  alerting back to disabled.
- [x] Verified live against real Redis and a real HTTP webhook receiver: created a tenant via `Management` with
  a 100-token quota and thresholds `[50, 90]` pointed at a local webhook receiver; seeded the tenant's real
  rate-limit Redis counter (same technique as Phase 6's live verification) to simulate usage, then sent a real
  chat-completion request through `Api` — confirmed the receiver got a real chunked-encoding HTTP POST with the
  exact expected JSON payload. Confirmed crossing two thresholds in a single check fires only the higher one
  (not both), that a second request within the same window doesn't re-fire, and — observed incidentally via
  real outbound-network latency pushing a follow-up request into the next window — that a new window correctly
  resets and re-alerts.
- [x] Live testing also caught and fixed a real, unrelated latent bug: `Dashboard`'s `vite.config.ts` dev proxy
  target was `http://localhost:5299`, but `Management`'s actual dev port (`launchSettings.json`) is `5162` —
  the Dashboard would have 404'd on every API call in local dev. `CLAUDE.md`'s own command walkthroughs had the
  same stale `5299`/`5298` typo, copy-pasted from the wrong source; both fixed.

## Phase 10 — Deployment artifacts
- [x] Scope narrowed by explicit direction: this environment has no real Azure subscription/credentials, so the
  deliverable is deployment *artifacts* (Dockerfiles, IaC, CD pipeline) verified as thoroughly as possible
  locally — Docker builds, `docker compose` end-to-end runs, `az bicep build` syntax validation — rather than an
  actual cloud deployment, which needs to happen separately with real credentials.
- [x] `src/Api/Dockerfile`, `src/Management/Dockerfile` (multi-stage, build context `src/` for the shared `Core`
  project dependency) and `src/Dashboard/Dockerfile` (node build → nginx runtime, build context
  `src/Dashboard`). Found and fixed a real bug while verifying these build: `src/.dockerignore` needed
  `**/bin`/`**/obj` (not just `bin`/`obj`, which only matches the context root) — without it, a developer's
  local build artifacts (host-absolute paths baked into MSBuild's generated files) got copied into the image and
  broke `dotnet publish` with a `NETSDK1064` error that looked like a restore problem but wasn't.
- [x] `Dashboard`'s production topology — an open question carried since Phase 8 — resolved: nginx
  (`nginx.conf.template`) reverse-proxies `/api/**` to `Management` same-origin, mirroring what
  `vite.config.ts`'s dev-server proxy already does locally. No `Cors:AllowedOrigins`/`SameSite=None`
  cross-origin cookie handling needed in either environment.
- [x] `deploy/main.bicep`: Container Apps environment + Log Analytics, Postgres Flexible Server, Azure Cache for
  Redis, a Key Vault for `AzureKeyVaultSecretStore` (tenant BYOK credentials — separate from the plain
  Container-App-secret infra secrets like the DB connection string), and the three Container Apps with managed
  identities granted Key Vault Secrets Officer. `az bicep build` clean (one syntax fix needed: an unescaped
  apostrophe inside a single-quoted `@description` string).
- [x] `.github/workflows/cd.yml`: builds + pushes all three images to GHCR on every push to `main` (harmless —
  publishes build artifacts only) plus a `deploy` job that updates the Container Apps to match, gated on an
  `AZURE_RESOURCE_GROUP` repo variable so it's a no-op (not a failure) until an environment actually exists to
  deploy to.
- [x] `docker-compose.full.yml`: a local smoke test running the actual images this pipeline builds, alongside
  the existing Postgres/Redis compose services. **Verified live**: all three images build; the stack boots and
  migrates; a real tenant-create → set-BYOK-credential → chat-completion-proxy flow round-trips correctly
  end-to-end through the containers, including confirming the cross-process `LocalDevSecretStore` Data
  Protection key-ring sharing (documented since Phase 4) also holds across separate *containers* on a shared
  volume, not just separate local processes.
- [x] `deploy/README.md` documents the full one-time-setup walkthrough (provision → migrate → wire GitHub↔Azure
  OIDC federated credentials → push) and known simplifications not hardened in this pass: Postgres firewall
  allows all Azure services rather than being VNet-scoped, `Management`'s Container App ingress is external
  rather than internal-only, no custom domain.

Deferred (not this phase, tracked in `ARCHITECTURE.md`'s open questions): actually running the deployment
against a real Azure subscription; billing/invoicing, pooled provider keys, per-tenant DB isolation for
compliance customers, additional providers (Azure OpenAI, etc.); the network-hardening items in
`deploy/README.md`'s "Known simplifications".
