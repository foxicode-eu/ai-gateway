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

Phases 0–10 are done (solution scaffolding, core domain + persistence, walking-skeleton proxy, multi-tenancy +
BYOK credentials, JWT/managed-IdP auth, streaming, rate limiting, observability + usage data, Dashboard, quota
alerting, deployment artifacts) — see `ROADMAP.md` for what's next. `Api` accepts both a JWT and a legacy hashed
API key (see "AuthN" below — this is a deliberate, temporary dual-scheme, not an oversight). `Management`
accepts both a session cookie and a bearer JWT — same deliberate dual-scheme idea, see "Sessions" below. Check
`ROADMAP.md` before starting new work so you're building the current phase, not a later one out of order, and
update it as phases complete. Update `ARCHITECTURE.md` too if you make a decision that resolves one of its "Open
questions". Note Phase 10's deployment artifacts (`deploy/`, the three `Dockerfile`s, `.github/workflows/cd.yml`)
were built and locally verified (`docker build`/`docker compose`/`az bicep build`) but **not exercised against a
real Azure subscription** — no cloud credentials are available in this environment. See `deploy/README.md`.
Separately, `aspire/AppHost` is a .NET Aspire + YARP local-orchestration layer (one `dotnet run` → Postgres,
Redis, `Api`, `Management`, Dashboard, all fronted by a single unified URL) — not part of `src/Gateway.slnx`,
see `aspire/README.md`.

## Solution structure

`src/Gateway.slnx` (new `.slnx` format) references all projects below. All .NET projects target `net10.0` with
nullable reference types and implicit usings enabled.

- `src/Api/` — ASP.NET Core Web API (`Microsoft.NET.Sdk.Web`), the data-plane proxy. References `Core`, wired
  up to Postgres via `AddGatewayPersistence`, providers via `AddProviderClients`, secrets via
  `AddGatewaySecrets`, `AddApiKeyAuthentication` + `AddManagedIdentityAuthentication`, rate limiting via
  `AddGatewayRateLimiting` + a scoped `RateLimitGate`, and observability via `AddGatewayObservability` + a scoped
  `UsageEventRecorder`. Endpoints:
  - `GET /.well-known/ai-routing-configuration` — stub, unimplemented.
  - `POST /v1/chat/completions` (`Endpoints/ChatCompletionsEndpoint.cs`) — requires `Authorization: Bearer
    <credential>`, enforced by `.AddEndpointFilter<TenantAuthenticationFilter>()`
    (`Authentication/TenantAuthenticationFilter.cs`). The credential is either a JWT (3 dot-separated segments —
    validated via `IJwtAccessTokenValidator`, tenant resolved from its `tenant_id` claim, then checked against
    real tenants in the DB) or a legacy hashed API key (`IApiKeyAuthenticator`, from Phase 3) — see "AuthN" below
    for why both exist. The filter stashes an `Authentication/AuthenticatedTenant` (`TenantId` + optional
    `ApiKeyId`, the latter only ever set for the legacy scheme) in `HttpContext.Items`. Once the tenant is
    resolved: `RateLimitGate.CheckAsync` (see "Rate limiting" below) — `429` if over quota; pick a provider from
    the model name (`Core.Providers.ProviderRouting` — currently a `"claude*" → anthropic, else → openai`
    heuristic, not real per-tenant config; see its doc comment); look up that tenant's BYOK credential via
    `ISecretStore`; then either `CreateChatCompletionAsync` (buffered `Results.Json`) or, if `stream:true`,
    `StreamChatCompletionAsync` via `Results.Stream(..., contentType: "text/event-stream")` — see "Streaming"
    below. Every exit point (rate-limited, no credential, completed) funnels through one local `FinishAsync`
    helper in the handler that records metrics, a trace span status, a `UsageEvent` row, and rate-limit usage —
    see "Observability & usage data" below — so those four things can't drift out of sync with each other.
- `src/Management/` — ASP.NET Core Web API (`Microsoft.NET.Sdk.Web`), the control-plane API. References `Core`,
  wired the same way as `Api` (persistence, providers, secrets, `AddManagedIdentityAuthentication` — no
  `AddApiKeyAuthentication` or rate limiting; Management doesn't proxy chat requests), plus session support via
  `AddGatewaySessions` + a scoped `SessionCookies`, plus CORS (`AddCors`, policy name `"Dashboard"` — empty
  `Cors:AllowedOrigins` by default, since local dev doesn't need it at all; see "Sessions" below). Every request
  runs `TenantScope.Unscoped` (set by middleware in `Program.cs`) — Management is the trusted control plane and
  operates across tenants by design. `POST /auth/login`, `POST /auth/logout`, `GET /auth/session`
  (`Endpoints/AuthEndpoint.cs`) are unauthenticated (that's what gets you authenticated — see "Sessions" below).
  All `/tenants/**` routes are grouped (`app.MapGroup("/tenants")` in `Program.cs`) and require either a valid
  session cookie or a bearer JWT via `.AddEndpointFilter<AdminAuthenticationFilter>()`
  (`Authentication/AdminAuthenticationFilter.cs`) — **any** authenticated caller is trusted as a superadmin able
  to operate on any tenant; there's no per-tenant admin restriction yet (open item in `ARCHITECTURE.md`).
  `/healthz` is outside the group and stays unauthenticated. Endpoints under `/tenants` (all in `Endpoints/`,
  registered with paths *relative* to the group — e.g. `""` for the group root, `/{tenantId:guid}` for a
  specific tenant):
  - `POST /tenants`, `GET /tenants` (list, ordered by name), `GET /tenants/{tenantId}`, `PATCH /tenants/{tenantId}`
    — tenant CRUD (no delete). Create and patch both accept an optional `tokenQuotaPerWindow` (null = unlimited;
    `PATCH` with `null` clears it back to unlimited — it's not "unset means don't change", the field is always
    applied as given) plus optional `alertWebhookUrl`/`alertThresholdPercentages` (same "always applied as
    given" rule — see "Quota alerting" below). `alertWebhookUrl` must be an absolute `http(s)` URL if given;
    each threshold must be `1`–`100`.
  - `POST /tenants/{tenantId}/api-keys`, `GET /tenants/{tenantId}/api-keys` (list — never the hash or plaintext),
    `PATCH /tenants/{tenantId}/api-keys/{apiKeyId}` — issues a key (the plaintext is only ever in the create
    response — `ApiKeyGenerator.GenerateSecret()`/`.Hash()` in `Core/Security`, the DB stores only the hash) and
    updates its own optional `tokenQuotaPerWindow`.
  - `DELETE /tenants/{tenantId}/api-keys/{apiKeyId}` — revokes (soft: sets `RevokedAtUtc`).
  - `PUT /tenants/{tenantId}/providers/{providerName}` — stores a tenant's BYOK credential for that provider via
    `ISecretStore`, keyed by `ProviderCredentialSecretName.For(tenantId, providerName)`.
  - `GET /tenants/{tenantId}/providers` — lists every known provider with a `configured: bool` — **never** the
    credential value itself, that's write-only through this API.
  - `GET /tenants/{tenantId}/usage?sinceHours=<n>` (default 24) — aggregate usage over the window: total
    requests/tokens/errors plus a per-provider breakdown. Reads `UsageEvent` rows `Api` writes — see
    "Observability & usage data" below. Rendered by `Dashboard`'s usage chart.
- `src/DevTools/` — a small console app, **local development only**. `dotnet run --project src/DevTools --
  mint-token <tenant-id-guid>` mints a JWT signed with the same `Authentication:StaticKey` config `Api`/
  `Management` use, for manually testing authenticated `curl` requests. Deliberately not an HTTP endpoint on the
  running gateway — see "AuthN" below for why that matters. Not referenced by `Api`/`Management`/`Dashboard`.
- `src/Core/` — class library for shared domain code used by both `Api` and `Management`:
  - `Auth/` — the managed-IdP JWT validation layer: `AuthenticationOptions` (config key `Authentication`,
    `Mode` = `"OidcAuthority"` for a real IdP via its OIDC discovery document, or `"StaticKey"` for local dev/
    tests — see below), `IJwtAccessTokenValidator`/`JwtAccessTokenValidator`, `LocalDevTokenIssuer` (mints
    `"StaticKey"`-signed tokens — throws if `Mode` isn't `"StaticKey"`, refusing to be pointed at a real IdP),
    and `AddManagedIdentityAuthentication` (DI registration). **The gateway never issues tokens over the wire**
    — `LocalDevTokenIssuer` is only ever called from tests and the offline `DevTools` CLI, never from an HTTP
    endpoint in `Api` or `Management`; keep it that way if you touch this area. In `"OidcAuthority"` mode,
    signing keys come from `{Authority}/.well-known/openid-configuration` via
    `ConfigurationManager<OpenIdConnectConfiguration>` (cached/auto-refreshed), the same mechanism ASP.NET
    Core's own JWT bearer handler uses — not hardcoded, not reinvented crypto.
  - `Entities/` — `Tenant`, `ApiKey` (each with a nullable `TokenQuotaPerWindow` — see "Rate limiting" below;
    null = unlimited), `UsageEvent` (see "Observability & usage data" below).
  - `RateLimiting/` — see the dedicated "Rate limiting" section below.
  - `Observability/` — see the dedicated "Observability & usage data" section below.
  - `Sessions/` — see the dedicated "Sessions" section below.
  - `Alerting/` — see the dedicated "Quota alerting" section below.
  - `Persistence/` — `GatewayDbContext` (EF Core + Npgsql), `GatewayDbContextFactory` (design-time factory for
    `dotnet ef` tooling), `ServiceCollectionExtensions.AddGatewayPersistence` (DI registration), and
    `Migrations/` (EF Core migrations — commit these, don't hand-edit generated migration files).
  - `Tenancy/` — the multi-tenancy scoping mechanism: `TenantScope`/`TenantScopeMode` (`Blocked`,
    `SingleTenant`, `Unscoped`) and `ICurrentTenantAccessor` (default impl: `AsyncLocalCurrentTenantAccessor`).
    `GatewayDbContext` applies a global query filter on tenant-owned entities (`ApiKey`, `UsageEvent`) keyed off
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
    heuristic mentioned above), `OpenAiProviderClient`, `IStreamResponseWriter` (see "Streaming" below), and
    `AddProviderClients`/`AddOpenAiProviderClient`/`AddAnthropicProviderClient` (DI registration). No credential
    is baked into any client at construction time — both `CreateChatCompletionAsync` and
    `StreamChatCompletionAsync` take the tenant's BYOK key per call, since one client instance is shared across
    every tenant using that provider.
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
    - `Providers/Anthropic/` — `AnthropicProviderClient`, `AnthropicChatTranslator` (pure functions translating
      the gateway's OpenAI-shaped public contract to/from Anthropic's native Messages API, non-streaming — moves
      `system` messages out of the array, defaults `max_tokens` when the caller omits it, maps `stop_reason` to
      OpenAI's `finish_reason` vocabulary), and `AnthropicStreamTranslator` (the streaming counterpart — see
      "Streaming" below). Known limitations documented on the translator methods: string message content only
      (no multimodal), and only text content blocks are read back from the response.

### Streaming

`IProviderClient.StreamChatCompletionAsync(request, apiKey, writer, cancellationToken)` writes OpenAI-shaped SSE
(`data: {...}\n\n`, terminated by `data: [DONE]\n\n`) to `writer.Body` as it arrives — never buffered in full.
- **OpenAI**: pure byte pass-through (the gateway's public contract already *is* OpenAI's SSE shape) — no
  per-chunk translation. Forces `stream_options.include_usage: true` on the outgoing request so the final chunk
  carries usage, which is extracted without a second round trip.
- **Anthropic**: real translation, one Anthropic SSE frame (`event: ...` + `data: ...`) at a time, via
  `AnthropicStreamTranslator` (`Core/Providers/Anthropic/`) — pure/stateful, no I/O, so it's unit-tested directly
  without any HTTP mocking (`Core.Tests/Providers/Anthropic/AnthropicStreamTranslatorTests.cs`). Maps
  `message_start` → an initial `{"delta":{"role":"assistant"}}` chunk, `content_block_delta` (text only) →
  content chunks, `message_delta`'s `stop_reason` → a finish-reason chunk, `message_stop` → `[DONE]`. A
  mid-stream `event: error` has no standard OpenAI streaming equivalent and is currently just dropped — real
  providers signal request-level failures as a non-streaming error (see next point), not mid-stream, so this
  hasn't mattered in practice.
- **`IStreamResponseWriter`** (`Core/Providers/IStreamResponseWriter.cs`) exists so `Core` doesn't need an
  ASP.NET Core dependency, and — more importantly — so a provider client can switch the *outer* HTTP response
  to a normal non-streaming JSON error (real status code + `application/json`) if the provider rejects the
  request (e.g. bad credentials) *before* sending any SSE data, rather than pretending a failed request was a
  successful 200 stream. This only works because it happens before the first write to `writer.Body` — headers
  can't change after that. `Api/Endpoints/ChatCompletionsEndpoint.cs` has a private `HttpResponseStreamWriter`
  adapting `HttpResponse` to this interface, used inside the `Results.Stream(...)` callback. **Verified live**
  against real `api.openai.com`/`api.anthropic.com` with intentionally-invalid keys: both correctly return the
  real status code (`401`) with `Content-Type: application/json` (not `text/event-stream`), and the Anthropic
  error body is correctly translated into the OpenAI-shaped `{"error": {...}}` form.
- Token usage is logged (`ILogger`, Information level) once a stream completes successfully, and fed into
  `RateLimitGate.RecordUsageAsync` (see "Rate limiting" below). There's still no usage-metrics consumer beyond
  rate limiting (that's Phase 7).

### Sessions

The Dashboard authenticates to `Management` with a server-side session, not a JWT stored in the browser — a
deliberate choice to reduce token-leakage surface on the client (the user's explicit direction for this phase).
`Core/Sessions`: `ISessionStore` (`SetAsync`/`GetAsync`/`RemoveAsync`, keyed by opaque session ID) with two
implementations selected by `Sessions:Store` config via `AddGatewaySessions` — same provider-swap pattern as
`Secrets`/`RateLimiting`:
- `RedisSessionStore` ("Redis") — production/local-dev path, keys `session:{sessionId}`.
- `InMemorySessionStore` ("InMemory") — `ConcurrentDictionary`, dev/test-only, takes `TimeProvider` for
  deterministic expiry tests.

`GatewaySessionOptions` (config key `Sessions`) — **named `Gateway`-prefixed, not just `SessionOptions`**,
because `SessionOptions` collides with an ASP.NET Core built-in type that's implicitly in scope via global
usings in a `Microsoft.NET.Sdk.Web` project (`CS0104` ambiguous reference — hit this for real, keep the prefix).
Fields: `Store`, `RedisConnectionString`, `IdleTimeoutMinutes` (default 480), `CookieName` (default
`ai_gateway_session`), `CookieSecure` (default `true` — set `false` in local dev over plain HTTP).

`Management/Authentication/SessionCookies.cs` is the piece that actually touches `HttpContext`:
`SignInAsync` generates a 32-byte random session ID (`RandomNumberGenerator`), stores the subject in
`ISessionStore`, and appends the cookie (`HttpOnly=true`, `SameSite=Lax`, `Secure` from options). `SignOutAsync`
removes the store entry and deletes the cookie. `IsValidAsync` reads the cookie and checks the store. Stealing
the cookie alone isn't enough to forge a session (there's no signed payload to replay) and a session is
server-side revocable — both properties a bare JWT-in-cookie wouldn't have.

`AdminAuthenticationFilter` checks the session cookie first, falling back to the existing bearer-JWT path
(`IJwtAccessTokenValidator`) if there's no valid session — this dual scheme is intentional, not a leftover: the
Dashboard uses the session, `curl`/`DevTools`/automation keep using bearer JWTs. `Endpoints/AuthEndpoint.cs`
(`POST /auth/login` — exchanges a JWT for a session cookie, `POST /auth/logout`, `GET /auth/session`) is the
only unauthenticated surface on `Management`, by necessity. For now the credential exchanged at `/auth/login`
is still a `DevTools`-minted JWT (`"StaticKey"` mode); the point of building it as a token-exchange rather than
hardcoding a login form against `LocalDevTokenIssuer` is that swapping in a real IdP later (OIDC login flow →
callback → exchange the IdP's token for a session the same way) doesn't require touching `SessionCookies` or
the session store at all.

CORS (`Management/Program.cs`, `AddCors` policy `"Dashboard"`) is wired but `Cors:AllowedOrigins` is empty by
default — local dev doesn't need it because the Vite dev server proxies `/api/**` to `Management`, making
Dashboard↔Management same-origin and sidestepping `SameSite=None`/`Secure`-over-HTTP cookie complexity
entirely. Production topology (same-origin via a reverse proxy vs. cross-origin requiring `Secure`+`SameSite=None`
cookies and a real `AllowedOrigins` list) is an open question — see `ARCHITECTURE.md`.

**Real bug found via live browser testing, not caught by any unit/integration test:** `LoginPage.tsx`'s login
mutation calls `queryClient.setQueryData(['session'], {authenticated: true})` in `onSuccess`, then originally
called `navigate({to: '/'})` right after `login()` resolved. But that query-cache write doesn't synchronously
flush a re-render before the subsequent `navigate()` call lands — `RequireAuth` on the newly-mounted `/` route
observed the *stale* `isAuthenticated: false` and immediately bounced back to `/login`, even though the login
itself had fully succeeded (cookie set, session in Redis). Fix: `LoginPage` no longer navigates imperatively;
it has a `useEffect` that navigates to `/` only when `isAuthenticated` reactively becomes `true` — one source of
truth instead of two code paths racing. Only surfaced through a real headless-browser session (Playwright)
driving an actual login → navigation sequence; the underlying endpoints and cookie mechanics tested correctly in
isolation both in `Management.Tests` and via `curl`.

### Rate limiting

`Core/RateLimiting`: `ITokenRateLimiter`/`TokenRateLimiter` (the sliding-window-counter algorithm — a weighted
blend of current + previous fixed-window counts, O(1) store ops per check, the standard practical approximation
of a true sliding window; see the class doc comment) built on `IRateLimitStore` (`RedisRateLimitStore` for
production, `InMemoryRateLimitStore` for local dev/tests — selected by `RateLimiting:Store` config, same
provider-swap pattern as `Secrets`). `AddGatewayRateLimiting` wires it up; only `Api` uses it (`Management`
doesn't proxy chat requests, so it has no rate limiting to enforce).

`Api/RateLimiting/RateLimitGate.cs` is the piece that actually resolves configured quotas from the DB
(`Tenant.TokenQuotaPerWindow`, `ApiKey.TokenQuotaPerWindow` — both nullable, null = unlimited) and calls the
limiter. `CheckAsync` returns a `RateLimitCheckResult` carrying the resolved quota values so `RecordUsageAsync`
doesn't need a second DB round trip to know whether either counter should be written to. Quota check happens
*before* proxying to the provider (necessarily an estimate — actual token count isn't known until the
completion finishes); usage is recorded *after*, extracted from the response's `"usage"` object (present in
every real OpenAI-shaped response, both native and Anthropic-translated) for non-streaming, or from the
`StreamUsage` returned by `StreamChatCompletionAsync` for streaming. A request that errors before producing a
`usage` object (bad BYOK credential, provider rejects it, etc.) records zero usage — quota tracks actual
consumption, not attempts.

**Verified live against real Redis** (not just the `InMemory` test double): booted `Api` against the
`docker-compose` Redis service, created a tenant with a small quota via `Management`, manually seeded a Redis
counter key past the quota threshold (`ratelimit:tenant:{tenantId:N}:{windowIndex}` — matching the format
`TokenRateLimiter` actually uses) and confirmed a live request was correctly blocked with `429`; confirmed a
tenant with no configured quota is not blocked and reaches the real provider host. Full consumption-tracking
(record → later check sees updated usage) is covered in `Api.Tests` against `InMemoryRateLimitStore` — doing the
same live wasn't practical without a real provider API key, since a failed provider call (the only kind
possible with a fake key) never produces token usage to record.

### Quota alerting

Opt-in per tenant: `Core.Entities.Tenant.AlertWebhookUrl` (null = alerting disabled) and
`AlertThresholdPercentages` (`int[]`, e.g. `[80, 100]` — percentages of `TokenQuotaPerWindow`; meaningless
without a quota also configured, since there's nothing to be a percentage of). Configured via `Management`'s
`PATCH /tenants/{id}` (see above) and the Dashboard's "Token quota & alerts" card (`QuotaCard.tsx` — one form,
one Save button, covering quota + webhook URL + comma-separated thresholds together, specifically so a plain
quota edit can't accidentally null out the alert fields via the "always applied as given" `PATCH` semantics).
Webhook delivery only for now — email was the other option on the table, deferred, see `ARCHITECTURE.md`'s open
questions.

`Core/Alerting`: `IQuotaAlertSender`/`WebhookQuotaAlertSender` (POSTs a `QuotaAlertPayload` — tenant ID,
crossed-threshold percentage, usage percentage, quota limit, current usage, timestamp — as JSON to the tenant's
webhook URL via a plain `HttpClient`, no `BaseAddress` since the URL is per-tenant and arbitrary, not a fixed
provider host) and `AddGatewayAlerting` (DI registration, `Api`-only — `Management` doesn't proxy chat requests
so has nothing to alert on).

`Api/Alerting/QuotaAlertGate.cs` is the piece that actually decides whether to fire, called from
`ChatCompletionsEndpoint`'s `FinishAsync` right after `RateLimitGate.RecordUsageAsync` on every request exit
path (so alerting can never itself skip a request that skipped usage recording, or vice versa). It re-checks the
tenant's usage via `ITokenRateLimiter.CheckAsync` against the *same* Redis/in-memory key `RateLimitGate` enforces
admission with (`Core.RateLimiting.RateLimitKeys.TenantKey` — extracted to its own class specifically so the two
call sites can't drift apart on the key format, the same kind of format-string-must-match gotcha `CLAUDE.md`
already flags for `RateLimitGate`/`TokenRateLimiter`), so it's reusing the same blended sliding-window estimate,
not a second independent counter. If usage now sits at or above the *highest* configured threshold that's been
crossed, that's the one threshold that fires — crossing 80% and 100% in the same check (e.g. a big single
request) fires once for 100%, not twice.

**Anti-duplicate-per-window state**: `IRateLimitStore` only exposes increment/get, no "set" — so "don't re-alert
for a threshold (or lower) already fired this window" is tracked in a `alert:tenant:{tenantId:N}:{windowIndex}`
key (same store, same window-index formula as `TokenRateLimiter`, computed independently since that formula is
private to `TokenRateLimiter`) whose value is pushed up to the newly-crossed threshold via `IncrementAsync(...,
crossedThreshold - alreadyAlerted, ...)` — the only way to express "move this counter up to at least X" with an
increment-only primitive. A new window (naturally, since the key is namespaced by `windowIndex`) starts this at
zero again, so alerting resets exactly the way rate-limit quotas do.

A webhook delivery failure (unreachable host, non-2xx response) is logged (`ILogger`, Warning level) and
swallowed inside `QuotaAlertGate` — a tenant's misconfigured webhook must never break the chat-completion request
that triggered the check.

**Verified live against real Redis and a real HTTP webhook receiver**: created a tenant via `Management` with a
100-token quota and thresholds `[50, 90]` pointed at a local webhook receiver; manually seeded the *same* Redis
rate-limit counter key `RateLimitGate` uses (mirroring the Phase 6 live-verification approach) to 55/100 tokens,
then sent one real chat-completion request through `Api` — confirmed the receiver got a real chunked-encoding
POST with the exact expected JSON payload (`thresholdPercentage: 50`). Re-seeded to 92/100 (past both
thresholds) and confirmed exactly one alert fired, for the *higher* threshold (`90`), not two. A second request
within the same rate-limit window did not re-fire; a request landing in the next window (observed incidentally,
via real outbound-network latency to `api.openai.com` pushing a follow-up request past a window boundary) did
correctly fire again — confirming the per-window reset works as designed, not just the same-window dedup path.

### Observability & usage data

`Core/Observability`: `AddGatewayObservability(configuration, serviceName)` wires up OpenTelemetry — ASP.NET
Core + HttpClient auto-instrumentation (so every inbound request and every outbound call to a provider gets a
trace span automatically, e.g. the `HttpClient` span to `api.openai.com` shows up as a child of the request that
triggered it) plus `GatewayDiagnostics`'s custom `AiGateway` `ActivitySource`/`Meter`
(`gateway.requests` counter, `gateway.request.duration` histogram, `gateway.tokens` counter — tagged
`tenant_id`/`provider`/`model`/`status_code`, or `tenant_id`/`token_type` for the token counter). Exporter is
`Observability:Exporter` = `"Otlp"` (production, a real collector at `Observability:OtlpEndpoint`) or
`"Console"` (local dev — prints traces/metrics to stdout, which is how this was actually verified live: booted
`Api`, made a real request, and confirmed the console output showed the custom `chat.completion` span correctly
nested under the ASP.NET Core request span, the OpenAI `HttpClient` span nested under *that*, and
`gateway.requests`/`gateway.request.duration` emitted with the expected tags). Both `Api` and `Management` call
`AddGatewayObservability`, but only `Api` emits `GatewayDiagnostics` metrics — `Management` gets the generic
ASP.NET Core instrumentation only, since it doesn't proxy chat requests.

Payload privacy is load-bearing here, not just a policy statement: nothing in any span tag, metric tag, or log
message carries prompt/completion content — only identifiers, counts, and status codes. Keep it that way if you
add more instrumentation.

`Core.Entities.UsageEvent` / `Api/Observability/UsageEventRecorder.cs`: one row per chat completion request that
reached tenant+provider resolution (requests that fail body validation before that — bad JSON, missing `model`
— aren't recorded; there's no tenant/provider context for them yet). `ChatCompletionsEndpoint.HandleAsync` has a
single local `FinishAsync` helper that every exit point (rate-limited `429`, no-credential `400`, and the
completed/streamed cases) funnels through — it's the one place that records the metric, closes out the trace
span, writes the `UsageEvent`, and calls `RateLimitGate.RecordUsageAsync`, specifically so those four things
can't drift out of sync by one exit path forgetting one of them.

`Management`'s `GET /tenants/{tenantId}/usage` endpoint (`Endpoints/UsageEndpoint.cs`) aggregates `UsageEvent`
rows over a configurable window (`?sinceHours=`, default 24): total requests/tokens/errors, plus a per-provider
breakdown. **Verified live end-to-end**: made a real (failing, fake-key) request through `Api`, confirmed a
`usage_events` row landed in Postgres with the correct provider/model/status code, and confirmed `Management`'s
usage endpoint reflected it correctly (1 request, 1 error, grouped under `"openai"`).

- `src/Dashboard/` — React 19 + TypeScript SPA (Vite), for tenant self-service against `Management`. Stack:
  TanStack Router (code-based routes in `src/routes.tsx`, not file-based — `@tanstack/router-plugin` was
  deliberately not installed), TanStack Query (`src/lib/auth.tsx`, per-page data hooks), shadcn/ui + Tailwind
  CSS v4 (`src/components/ui/*`, manually authored — not CLI-scaffolded, for environment-reliability reasons —
  `src/index.css` has the theme tokens), Recharts (usage chart). `vite.config.ts` proxies `/api/**` to
  `Management` (`http://localhost:5162`) so the browser talks same-origin — see "Sessions" above for why that
  matters for cookies. `server.allowedHosts: true` is set because Vite 5+ rejects requests with an unrecognized
  `Host` header by default, and when this dev server is run behind the `aspire/` AppHost's YARP gateway (see
  Commands below), the proxied request's `Host` is Aspire's own internal service-discovery hostname, not
  `localhost` — harmless to leave permissive since this dev server was never reachable from outside the host
  machine anyway. `src/lib/api.ts` is a thin `fetch` wrapper (`credentials: 'include'` on every call,
  `ApiError` with a `status` for callers to branch on 401s) with one function per `Management` endpoint.
  `src/lib/auth.tsx`'s `AuthProvider`/`useAuth()` wraps the `GET /auth/session` query plus login/logout
  mutations; `RequireAuth.tsx` redirects unauthenticated users to `/login`. Pages: `LoginPage`, `TenantsListPage`
  (list + create-tenant dialog), `TenantDetailPage` (composes `QuotaCard`/`ApiKeysCard`/`ProvidersCard`/
  `UsageCard` under `pages/tenant-detail/` — `QuotaCard` handles both the token quota and its alert
  webhook/thresholds in one form, see "Quota alerting" below for why they're not split across two saves). No
  automated test suite yet (open item in `ARCHITECTURE.md`) — verified so far by `npm run build`/`npm run lint`
  plus live headless-browser driving (see "Sessions" above for the one real bug that surfaced that way).
  **Real bug found via the same live-driving process, Phase 9**: `vite.config.ts`'s dev proxy target was
  `http://localhost:5299`, but `Management`'s actual `launchSettings.json` dev port is `5162` (`5299`/`5298` only
  ever existed in a stale copy-paste in this file's own command walkthroughs, now fixed) — the Dashboard would
  have 404'd on every `/api/**` call in local dev. Caught because live-driving the UI is a real HTTP round trip
  through the actual proxy, not a mock.
- `src/Core.Tests/` — covers the tenant query-filter behavior, both provider clients incl. streaming (fake
  `HttpMessageHandler` + `FakeStreamResponseWriter`, no real network calls — including the error-before-streaming
  path for both providers), the Anthropic translators, both non-streaming (pure-function tests) and streaming
  (`AnthropicStreamTranslatorTests` — pure/stateful, feeds a sequence of Anthropic SSE events and asserts the
  resulting OpenAI-shaped chunks and final usage), the provider-registration regression above, `ApiKeyGenerator`,
  `LocalDevSecretStore` (round-trip + cross-instance decryption using `EphemeralDataProtectionProvider`), and
  `JwtAccessTokenValidator`/`LocalDevTokenIssuer` (valid/expired/wrong-key/wrong-audience/wrong-issuer/malformed
  tokens, all in `"StaticKey"` mode — no network calls, no real IdP needed), `TokenRateLimiter` (against
  `InMemoryRateLimitStore` + a hand-rolled `ManualTimeProvider` test double — deterministic control over
  elapsed-window fraction, including the exact-boundary and decay-mid-window cases, without any real waiting),
  and `WebhookQuotaAlertSender` (fake `HttpMessageHandler` — asserts the JSON payload shape and that a non-2xx
  response throws, no real network call).
- `src/Api.Tests/` — `ChatCompletionsEndpointTests.cs`, a `WebApplicationFactory<Program>` integration test.
  Swaps in the EF Core InMemory provider for `GatewayDbContext` (see the `RemoveAll<DbContextOptions<...>>` +
  `RemoveAll<IDbContextOptionsConfiguration<...>>` + re-`AddDbContext` pattern — both removals are needed, or
  EF Core throws "two database providers registered" at runtime), a stub `IProviderClient`, a stub
  `ISecretStore`, and a test-owned `"StaticKey"` `AuthenticationOptions` override (via `services.Configure<AuthenticationOptions>`,
  applied *after* `Program.cs`'s own registration so it wins), and seeds a real tenant + API key through the
  `GatewayDbContext` directly so tests authenticate the same way a real client would — both the legacy API-key
  path and the JWT path (minted with `LocalDevTokenIssuer` against the test's own options) are covered, as is
  streaming (the stub `IProviderClient.StreamChatCompletionAsync` writes a canned SSE body via the real
  `IStreamResponseWriter` contract, plus a case exercising the "provider rejects before streaming" path through
  the actual `Results.Stream`/`HttpResponse` pipeline — not just a fake, since that's exactly where the tricky
  "can we still change status code from inside the stream callback" assumption lives), and rate limiting
  (overrides `IRateLimitStore` with `InMemoryRateLimitStore` to avoid a real Redis dependency in tests — tenant
  quota blocking, api-key quota blocking with room left on the tenant, no-quota-configured passthrough, and JWT
  auth not being subject to a per-key quota), usage-event persistence (asserts the `UsageEvent` row written
  for successful, streaming, rate-limited, and no-credential requests has the right tenant/provider/model/status/
  token/streamed values), and quota alerting (a stub `IQuotaAlertSender` capturing sent payloads — fires once a
  configured threshold is crossed, doesn't fire below it, doesn't re-fire the same threshold twice in one
  window, fires again for a *higher* threshold crossed in a later check, and never fires when no webhook is
  configured). Tests that reach into `GatewayDbContext` directly outside of an
  HTTP request (e.g. to set a quota mid-test) need `.IgnoreQueryFilters()` on `ApiKeys` queries — there's no
  ambient `TenantScope` in a bare `CreateScope()`, so the fail-closed default (`Blocked`) hides everything,
  same underlying mechanism as the `Tenancy` bullet above, just easy to trip over again in a new spot.
  `Program.cs` has `public partial class Program;` at the bottom specifically to make it accessible to
  `WebApplicationFactory<Program>` — keep that if you touch `Program.cs`.
- `src/Management.Tests/` — `ManagementApiFactory` (shared `WebApplicationFactory<Program>`, same InMemory-DB
  swap pattern as `Api.Tests`, plus an in-memory `ISecretStore`, an `InMemorySessionStore` swapped in for
  `ISessionStore` to avoid a real Redis dependency in tests, and its own test-owned `"StaticKey"` auth config)
  exposes `CreateAuthenticatedClient()` (an `HttpClient` pre-authenticated with a JWT minted via
  `LocalDevTokenIssuer`) alongside the inherited `CreateClient()` for testing the unauthenticated case. Endpoint
  tests per resource (`TenantsEndpointTests`, `ApiKeysEndpointTests`, `ProviderCredentialsEndpointTests`,
  `UsageEndpointTests`) all use the authenticated client; each also has (or `TenantsEndpointTests` has, covering
  the shared filter) a test asserting `401` without a token, plus a test for its new `GET` list endpoint
  (tenants ordered by name; API keys never exposing the hash/plaintext; providers reporting `configured` without
  ever returning the secret value; 404s for an unknown tenant in each case). `UsageEndpointTests` seeds
  `UsageEvent` rows directly (bypassing `Api`) and checks the aggregation/grouping/time-window-filtering math.
  `AuthEndpointTests` covers the session lifecycle end-to-end within the test host: login with a valid token sets
  a cookie that authenticates subsequent requests, an invalid token is rejected with no cookie set, logout
  invalidates the session, `GET /auth/session` reflects current login state, and a bearer token still works with
  no session cookie present (the dual-scheme path). Since `WebApplicationFactory`'s client has no automatic
  cookie jar, these tests extract `Set-Cookie` from the login response and attach it manually via a `Cookie`
  header on subsequent requests. `TenantsEndpointTests` also covers alert-field validation and round-tripping:
  a non-absolute `alertWebhookUrl` and an out-of-range (outside `1`–`100`) threshold are both rejected with
  `400`, a valid webhook URL + threshold list round-trips through create/update/get, and a `PATCH` with both
  fields omitted clears alerting back to disabled (same "always applied as given" semantics as
  `tokenQuotaPerWindow`).
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

### Deployment

Containerized per `ARCHITECTURE.md`'s Deployment section: `src/Api/Dockerfile`, `src/Management/Dockerfile`
(both multi-stage, build context `src/` since they need the sibling `Core` project — only the two `.csproj`
files this specific image needs are copied before `dotnet restore`, so unrelated-project changes don't bust the
restore layer's cache) and `src/Dashboard/Dockerfile` (build context `src/Dashboard`, node build stage → static
files served by nginx). **Gotcha already hit once, twice actually — bites any multi-stage .NET Dockerfile built
from a parent directory:** `src/.dockerignore` must exclude `**/bin`/`**/obj`, not just `bin`/`obj` — the latter
only matches at the build-context root, so a developer's local `Api/bin`/`Core/obj` (host-absolute paths baked
into MSBuild's generated files) silently got copied into the image and broke `dotnet publish` with a
`NETSDK1064: Package ... was not found` error that looked like a restore problem but wasn't. Caught by actually
building the images, not just writing them.

`Dashboard`'s nginx (`nginx.conf.template`, templated via the nginx image's built-in envsubst-on-startup)
reverse-proxies `/api/**` to `Management` same-origin — the exact same choice `vite.config.ts`'s dev-server
proxy makes locally, and the resolution to `ARCHITECTURE.md`'s former "production Dashboard↔Management
topology" open question. `MANAGEMENT_UPSTREAM` (env var, full origin including scheme — `http://management:8080`
for `docker-compose.full.yml`, an `https://` Container Apps FQDN in Azure) is the one substituted template
variable; nginx's own variables (`$host`, `$uri`, ...) are untouched since they aren't container environment
variables envsubst's substitution list is built from.

`docker-compose.full.yml` is a local smoke test — the actual images above, wired to the same Postgres/Redis
`docker-compose.yml` already provides — distinct from `npm run dev`/`dotnet run` local dev. **Verified working**:
all three images build; the stack boots, migrates, and a real tenant-create → set-BYOK-credential →
chat-completion-proxy flow round-trips correctly, including cross-*container* Data Protection key-ring sharing
for `LocalDevSecretStore` (the same sharing `CLAUDE.md`'s `Secrets/` bullet already documents across separate
*processes* — confirmed it also holds across separate *containers* on a shared volume). Its
`Authentication__StaticKey__SigningKey` is a fixed, committed, dev-only value for this smoke test only.

`deploy/main.bicep` provisions the real Azure target (Container Apps environment, Postgres Flexible Server,
Azure Cache for Redis, Key Vault, the three Container Apps with managed identities granted Key Vault Secrets
Officer) and `.github/workflows/cd.yml` builds+pushes images to GHCR on every push to `main`, then updates the
Container Apps to match — **not run against a real subscription**, only `az bicep build`-validated for syntax
and structurally reviewed; see `deploy/README.md` for the full setup walkthrough, prerequisites, and known
simplifications. The CD workflow's `deploy` job is gated on an `AZURE_RESOURCE_GROUP` repo variable being set,
so pushing to `main` today only builds+pushes images (harmless) and never attempts a deploy no environment has
actually been provisioned for.

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

### Local Postgres + Redis + EF Core migrations

`docker-compose.yml` (repo root) runs local Postgres and Redis for development, matching
`src/Api/appsettings.Development.json` / `src/Management/appsettings.Development.json`
(Postgres: `Host=localhost;Port=5432;Database=ai_gateway;Username=ai_gateway;Password=ai_gateway`; Redis:
`localhost:6379` — dev-only credentials, no auth on the local Redis).

```bash
docker compose up -d          # start local Postgres + Redis (from repo root)

# EF Core tooling (dotnet-ef is a local tool — see .config/dotnet-tools.json; run `dotnet tool restore` once)
dotnet ef migrations add <Name> --project src/Core --startup-project src/Core --output-dir Persistence/Migrations
dotnet ef database update --project src/Core --startup-project src/Core
```

`GatewayDbContextFactory` (used by the `dotnet ef` commands above) reads the connection string from the
`GATEWAY_DB_CONNECTION_STRING` env var, falling back to the same local dev default if unset.

Both dev configs also default `Observability:Exporter` to `"Console"` — running either service locally prints
OpenTelemetry trace/metric output to stdout, no collector needed. Switch to `"Otlp"` (+ `Observability:OtlpEndpoint`)
to point at a real collector.

To inspect rate-limit counters directly: `docker exec ai-gateway-redis-1 redis-cli KEYS 'ratelimit:*'` (keys are
`ratelimit:tenant:{tenantId:N}:{windowIndex}` / `ratelimit:apikey:{apiKeyId:N}:{windowIndex}`, see
`RateLimitGate`/`TokenRateLimiter`). Quota-alert anti-duplicate state lives alongside it under
`alert:tenant:{tenantId:N}:{windowIndex}` (see "Quota alerting" above) — same store, same `KEYS 'alert:*'` to
inspect. To trigger a real alert locally without a real provider API key (a failed provider call never produces
token usage to record on its own), seed the tenant's *rate-limit* counter directly to simulate usage past a
threshold, then send any request:
```bash
docker exec ai-gateway-redis-1 redis-cli SET "ratelimit:tenant:<tenantId:N>:<windowIndex>" 92 EX 120
```
(`windowIndex` = `unix-seconds / RateLimiting:WindowSeconds`, `tenantId:N` = the GUID with no dashes.)

CI (`.github/workflows/ci.yml`) runs `dotnet build`/`dotnet test` on the solution and `npm run build` on the
Dashboard for every PR. It does not currently run against a real Postgres instance or apply migrations — that's
an open gap, not a deliberate decision; revisit if/when integration tests need a real database in CI.

### Exercising the tenant onboarding → BYOK → proxy flow locally

With Postgres running (above) and both `Api` and `Management` started, `Management`'s `/tenants/**` routes need
a valid admin JWT — mint one with `DevTools` (any `tenant_id` claim works here; Management trusts any valid
token, see "AuthN" above):

```bash
ADMIN_TOKEN=$(dotnet run --project src/DevTools -- mint-token 00000000-0000-0000-0000-000000000000)

# create a tenant, issue an API key, set a provider credential
curl -X POST http://localhost:5162/tenants -H "Authorization: Bearer $ADMIN_TOKEN" -H "Content-Type: application/json" -d '{"name":"Acme"}'
curl -X POST http://localhost:5162/tenants/<tenantId>/api-keys -H "Authorization: Bearer $ADMIN_TOKEN" -H "Content-Type: application/json" -d '{"name":"prod"}'
curl -X PUT http://localhost:5162/tenants/<tenantId>/providers/openai -H "Authorization: Bearer $ADMIN_TOKEN" -H "Content-Type: application/json" -d '{"apiKey":"sk-..."}'

# optionally configure a token quota + quota-alert webhook (see "Quota alerting" above)
curl -X PATCH http://localhost:5162/tenants/<tenantId> -H "Authorization: Bearer $ADMIN_TOKEN" -H "Content-Type: application/json" \
  -d '{"tokenQuotaPerWindow":1000,"alertWebhookUrl":"https://example.com/hooks/quota","alertThresholdPercentages":[80,100]}'

# call the data-plane — either the issued API key, or a JWT scoped to that tenant, both work
curl -X POST http://localhost:5116/v1/chat/completions \
  -H "Authorization: Bearer <the key from api-keys response>" -H "Content-Type: application/json" \
  -d '{"model":"gpt-4o-mini","messages":[{"role":"user","content":"hi"}]}'

DATA_PLANE_TOKEN=$(dotnet run --project src/DevTools -- mint-token <tenantId>)
curl -X POST http://localhost:5116/v1/chat/completions \
  -H "Authorization: Bearer $DATA_PLANE_TOKEN" -H "Content-Type: application/json" \
  -d '{"model":"gpt-4o-mini","messages":[{"role":"user","content":"hi"}]}'

# check what got recorded
curl http://localhost:5162/tenants/<tenantId>/usage -H "Authorization: Bearer $ADMIN_TOKEN"
```

Both `Api` and `Management` default to `Secrets:Provider = "LocalDev"` in `appsettings.Development.json`, storing
encrypted credentials in `.local/secrets.dev.json` (gitignored) at the repo root — see the `LocalDevSecretStore`
note above for why the Data Protection key ring configuration there matters. If the two processes ever seem to
disagree about a stored credential (`CryptographicException` on `GetSecretAsync`), it usually means
`.local/secrets.dev.json` predates a change to that key-ring setup; deleting `.local/` and re-onboarding the
tenant is the fix, not debugging decryption further.

`DevTools` reads `Authentication:*` from `src/Api/appsettings.Development.json` by default (override with
`--config <path>`) — both `Api` and `Management`'s dev configs share the same `Authentication:StaticKey` value
so a token minted once works against either.

### Running the Dashboard locally

With Postgres/Redis up and `Management` running (above), start the Dashboard from `src/Dashboard`:

```bash
npm run dev   # http://localhost:5173, proxies /api/** to Management on :5162
```

Open `http://localhost:5173` — it redirects to `/login`. Paste a JWT minted the same way as the `curl` walkthrough
above (`dotnet run --project src/DevTools -- mint-token 00000000-0000-0000-0000-000000000000`) into the login
form; that exchanges it for a session cookie (see "Sessions" above) and lands on the tenant list. From there:
create a tenant, click into its detail page, issue an API key, set a provider credential, and the usage chart
renders once `Api` has recorded some `UsageEvent` rows for that tenant.

### Running the full containerized stack locally

A closer-to-production smoke test than the two commands above — runs the actual `Dockerfile`s (see
"Deployment" above), not `dotnet run`/`npm run dev`:

```bash
docker compose -f docker-compose.yml -f docker-compose.full.yml up --build -d
dotnet ef database update --project src/Core --startup-project src/Core   # first run only, applies migrations
```

`http://localhost:5116` (Api), `http://localhost:5162` (Management), `http://localhost:8081` (Dashboard, behind
its nginx reverse proxy). `docker compose -f docker-compose.yml -f docker-compose.full.yml down` to stop (add
`-v` only if you also want to wipe the Postgres/Redis/secrets volumes — that deletes local dev data, not just
this smoke test's).

### Running everything via .NET Aspire + YARP (single unified local URL)

`aspire/AppHost` is a third local-run option, distinct from both of the above: one `dotnet run` starts Postgres,
Redis, `Api`, `Management`, and the Dashboard's Vite dev server, applies EF Core migrations automatically, and
fronts all of it behind a single YARP gateway URL (`http://localhost:5100` — `/v1/**`/`/.well-known/**` →
`Api` unprefixed, `/api/**` → `Management` with the prefix stripped, everything else → the Dashboard). Not part
of `src/Gateway.slnx`.

```bash
cd aspire/AppHost
ASPIRE_ALLOW_UNSECURED_TRANSPORT=true dotnet run --launch-profile http
```

See `aspire/README.md` for the full picture, including two real bugs this orchestration surfaced that a plain
`curl`-for-200 check wouldn't have caught: Aspire's NodeJs hosting defaults child processes to
`NODE_ENV=production`, which silently disables Vite's React Fast Refresh preamble (page loads, but every
component throws `$RefreshSig$ is not defined` the instant a real browser evaluates it); and combining a
persistent Postgres data volume with Aspire's per-run auto-generated password means the *second* run's Postgres
container can't authenticate its own migrations job and everything downstream hangs forever waiting on it — only
caught by actually running the AppHost twice, not once. Every run starts from a fresh empty database (no
persistent volume, deliberately — see `aspire/README.md`); for data that survives across runs, use the
`docker-compose.yml` + `dotnet run` workflow above instead.
