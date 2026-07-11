# Architecture Plan

This document captures the intended architecture for AI Gateway. It reflects decisions made during planning;
implementation has not caught up to all of it yet (see `CLAUDE.md` for current build status). Treat this as the
target design to build towards, and update it as decisions change.

## Product summary

A multi-tenant, cloud-hosted SaaS gateway that companies route their AI usage through to reach inference
providers (Anthropic, OpenAI, ...). Client-facing endpoints are wire-compatible with the OpenAI REST API so
existing OpenAI SDKs work as a drop-in replacement. The gateway meters token usage, enforces rate limits, and
exposes usage/observability data back to tenants.

## Repo layout

Single repo, multiple .NET/JS projects under `src/`, registered in `src/Gateway.slnx`:

- **`src/Api`** — data-plane: the OpenAI-compatible proxy that tenant applications call at runtime
  (`/v1/chat/completions`, `/v1/completions`, `/v1/models`, ...). Optimized for latency; this is the hot path.
- **`src/Management`** — control-plane REST API: tenant onboarding, API key issuance, provider credential
  management, usage queries, quota configuration. Consumed by the dashboard and usable directly by tenants.
- **`src/Dashboard`** — React + TypeScript SPA (Vite) for tenant self-service (tenant/key/provider management,
  usage charts, quota-alert configuration). Talks only to `src/Management`, proxied same-origin in local dev
  (see CLAUDE.md). Stack: TanStack Router (code-based routing) + TanStack Query (server state) + shadcn/ui
  primitives on Tailwind CSS v4 + Recharts.
- **`src/Core`** (shared .NET library) — tenant/domain model, provider client abstractions, rate-limit
  primitives, usage-event schema. Referenced by both `Api` and `Management` so the data-plane and control-plane
  agree on tenant/quota semantics without duplicating logic.
- Test projects mirror each of the above (e.g. `src/Api.Tests`, `src/Core.Tests`) using xUnit.

Split `Api` and `Management` from day one rather than growing one project and splitting later: they have very
different latency/scaling profiles (proxy hot path vs. low-traffic admin CRUD) and different auth models
(OAuth2 client-credentials vs. OIDC user login), so keeping them separate avoids the two concerns tangling.

## Multi-tenancy

Shared PostgreSQL database, single schema, every tenant-owned table carries a `tenant_id` column. Use EF Core
global query filters keyed off the current request's tenant to prevent cross-tenant leaks by default rather than
relying on every query to remember a `WHERE tenant_id = ...` clause. Revisit schema- or database-per-tenant only
if a specific enterprise/compliance customer requires it — don't build that isolation generally up front.

## AuthN / AuthZ

- **Identity provider**: a managed IdP (e.g. Microsoft Entra ID external tenants, or Auth0) issues tokens; the
  gateway validates JWTs via standard JWT bearer middleware and never issues tokens itself. This keeps the
  gateway out of the business of storing credentials or implementing OAuth2 flows correctly.
- **Data-plane (`Api`)**: tenant applications authenticate proxied requests with OAuth2 client-credentials
  tokens scoped to a tenant and an API key. The API key identity is what rate limits and usage attribution key
  off (see below).
- **Control-plane (`Management`)**: tenant admin users authenticate via OIDC/SSO through the same IdP; dashboard
  sessions use standard authorization-code + PKCE flow. The `Dashboard` browser session itself is **server-side,
  cookie-based** — the browser is only ever handed an opaque, HttpOnly session ID, never the underlying JWT/IdP
  tokens. This is a deliberate choice (over, say, storing a JWT in the SPA and sending it as a bearer token)
  specifically to minimize token exposure to client-side JS/XSS: the actual credential never leaves the server,
  and a session can be revoked server-side without needing token blocklisting. See "Session infrastructure"
  below for how this is implemented and why it's already real infra, not a placeholder.

**Implementation status**: `Core/Auth` provides a generic, IdP-agnostic JWT bearer validator
(`IJwtAccessTokenValidator`) driven by `Authentication:Authority`/`Authentication:Audience` config — this works
against any OIDC-compliant IdP (Entra ID, Auth0, ...) once one is configured, since signing keys are fetched
from the IdP's own discovery document rather than hardcoded. It's applied to `Management` (any valid token is
trusted — see the per-tenant-admin gap below) and, on `Api`, is accepted *alongside* the Phase 3 hashed-API-key
scheme rather than replacing it.

That "alongside" is a deliberate, temporary compromise, not the target design: fully realizing "OAuth2
client-credentials tokens scoped to a tenant and an API key" requires the gateway to dynamically register an
OAuth2 client with the external IdP whenever `Management` issues an API key (so the IdP can mint a token
embedding that tenant's identity) — and that's IdP-specific integration work that needs a real IdP account to
build and verify against, which isn't available yet. Until that lands, `Api`'s hashed-API-key scheme (Phase 3)
remains the primary, fully-verified auth path; JWTs are accepted wherever a `tenant_id` claim happens to be
present (validated against real tenants in the DB), so the code is ready to receive real IdP-issued tokens the
moment dynamic client registration exists, without another breaking change.

Local development and tests use a "StaticKey" mode (`LocalDevTokenIssuer` in `Core/Auth`, plus the `DevTools`
CLI project) that mints tokens signed with a locally-configured symmetric key instead of a real IdP — this is
explicitly test/dev-only and is never how a real credential should be minted; the running gateway processes
never expose a token-issuing HTTP endpoint.

### Session infrastructure (Management + Dashboard)

`Management` exchanges a bearer JWT (validated through the same `IJwtAccessTokenValidator` described above —
today that's a `DevTools`-minted token, later a real IdP-issued one, with no change needed on this side of the
exchange) for a server-side session via `POST /auth/login`. The session itself lives in `Core/Sessions`
(`ISessionStore`, Redis-backed in production / in-memory for local dev and tests — the same provider-swap
pattern as `Secrets`/`RateLimiting`/`Observability`), keyed by a random opaque ID that's the *only* thing the
`Set-Cookie` response carries (`HttpOnly`, `SameSite=Lax`, `Secure` in non-dev environments). `Management`'s
existing bearer-JWT auth path (from Phase 4) is kept working alongside the cookie — `AdminAuthenticationFilter`
accepts either, so the `curl`/`DevTools` automation flow documented in CLAUDE.md doesn't need a session at all,
while the `Dashboard` always uses the cookie.

Swapping in a real IdP later only changes how the *Dashboard* obtains the JWT to exchange (an OIDC
authorization-code+PKCE redirect instead of a paste-a-token login form) — the exchange endpoint, the session
store, the cookie, and `AdminAuthenticationFilter` don't need to change. That's the concrete sense in which this
infrastructure is "in place for it," not just planned.

## Provider integration

- Initial providers: **Anthropic** (Messages API) and **OpenAI**. Design the provider abstraction
  (`IProviderClient` or similar in `src/Core`) so adding a provider doesn't require touching the public-facing
  routing/auth/rate-limit layers.
- **Credential model**: bring-your-own-key (BYOK) only, initially. Each tenant supplies their own provider API
  keys; the gateway uses them on the tenant's behalf and does not bear provider cost. Design the credential
  storage/lookup path so that pooled/gateway-owned keys (a future reseller tier) can be added later without a
  rework — e.g. don't hardcode the assumption that a key always belongs to exactly one tenant.
- **Secrets storage**: tenant provider API keys are stored via a cloud key vault (Azure Key Vault), not
  homegrown encryption in Postgres.
- **Public API surface**: OpenAI-compatible REST shape. Requests targeting non-OpenAI providers (e.g. Anthropic)
  are translated at the edge in `Api` so tenants can point any OpenAI-SDK-compatible client at the gateway
  regardless of which backend model/provider actually serves the request.
- **Streaming**: SSE streaming (`stream: true`) is supported end-to-end — the provider's stream is forwarded
  chunk-by-chunk, never buffered in full. OpenAI is a byte pass-through (the public contract already matches
  OpenAI's own SSE shape); Anthropic gets a real, frame-by-frame translation
  (`Core/Providers/Anthropic/AnthropicStreamTranslator`) since its native Messages API streaming format is
  unrelated to OpenAI's. Token usage is captured once the stream completes and logged — there's no
  rate-limiting/metrics consumer for it yet (Phases 6/7 build on this hook, they don't exist yet). If a provider
  rejects a request before sending any stream data (e.g. bad credentials), the gateway returns a normal
  non-streaming JSON error with the real status code rather than a 200 SSE stream containing an error — verified
  live against both real provider hosts with intentionally-invalid keys.

## Rate limiting

- Enforced via Redis-backed sliding-window counters (`Core/RateLimiting`) so limits are correct across multiple
  `Api` instances (required once the gateway scales beyond one replica). The algorithm is the standard
  weighted-blend "sliding window counter" approximation (two fixed-window counters, current weighted 1.0 and
  previous weighted by remaining overlap) rather than a full per-event log — O(1) store operations per check,
  at the cost of assuming usage within a window is spread evenly (it isn't exactly, but this is the accepted
  practical trade-off). Store is swappable (`RateLimiting:Store` = `"Redis"` production / `"InMemory"` local
  dev/tests, mirroring the `Secrets` provider pattern) — verified live against real Redis, not just the
  in-memory test double.
- Granularity: quotas apply **per tenant and per API key** — a tenant gets an overall quota
  (`Tenant.TokenQuotaPerWindow`), and can also set a finer-grained limit on individual API keys
  (`ApiKey.TokenQuotaPerWindow`), both nullable (null = unlimited), settable at creation and updatable via
  `PATCH` on `Management`. JWT-authenticated data-plane requests (see AuthN/AuthZ) have no notion of "which API
  key" — that credential model doesn't have one — so they're only subject to the tenant-level quota, not a
  per-key one.
- Flow: check remaining quota before proxying a request (an estimate, not a hard admission — actual token count
  isn't known until the completion finishes, streaming or not); after the provider responds (or the stream
  completes), record actual token usage against whichever counters (tenant, and API key if present) had a
  quota configured. A request that never resolves to a provider response (e.g. the provider itself rejects it,
  no BYOK credential configured) records zero usage — quota is about actual consumption, not attempts.

## Observability & usage data

- Instrumented with **OpenTelemetry** (`Core/Observability`) — ASP.NET Core + HttpClient auto-instrumentation on
  both `Api` and `Management`, plus a custom `AiGateway` `ActivitySource`/`Meter` for gateway-specific spans and
  metrics on `Api` (`Management` doesn't proxy chat requests, so it has nothing gateway-specific to emit).
  Exporter is swappable (`Observability:Exporter` = `"Otlp"` production / `"Console"` local dev — same
  provider-swap pattern used for `Secrets` and `RateLimiting`), so the backend (Azure Monitor, Grafana/
  Prometheus, Datadog, ...) can be swapped without re-instrumenting.
- Metrics tracked per tenant/provider/model/status code: request count, latency, tokens in/out (`gateway.requests`,
  `gateway.request.duration`, `gateway.tokens`). Not yet broken out per-API-key as a separate dimension — tenant
  is the primary axis today; revisit if per-key metric drill-down turns out to matter in practice. Tagging every
  metric with `tenant_id` is a known cardinality trade-off (one time series per tenant × provider × model ×
  status combination) — acceptable at this stage, worth revisiting if tenant count grows large.
- **Payload privacy**: only request/response *metadata* is stored (token counts, latency, model, status code) —
  both in trace/metric tags and in the persisted `UsageEvent` records below. Prompt and completion content is
  never persisted — this is a hard rule given the gateway handles arbitrary customer data across many tenants,
  not just a nice-to-have.
- **Usage-event persistence**: every chat completion request that resolves to a tenant + provider (i.e. not ones
  that fail basic request validation before that point) is recorded as a `UsageEvent` row — tenant, API key (if
  the legacy scheme was used), provider, model, streamed flag, status code, prompt/completion tokens, latency.
  Metadata only, same rule as above.
- Usage data feeds:
  - Per-tenant usage queries (`GET /tenants/{id}/usage` on `Management` — aggregate totals + a per-provider
    breakdown over a configurable time window), rendered as a bar chart in `Dashboard` (Recharts).
  - Quota-threshold alerting: opt-in per tenant (`Tenant.AlertWebhookUrl` + `Tenant.AlertThresholdPercentages`,
    configured via `PATCH /tenants/{id}` and the Dashboard's quota card). `Api.Alerting.QuotaAlertGate` checks,
    inline after every request records its usage, whether the tenant's estimated usage just crossed a configured
    percentage of `TokenQuotaPerWindow` and POSTs a JSON payload to the webhook via `Core.Alerting.WebhookQuotaAlertSender`
    if so — webhook-only for now (see the former open question below, now resolved). A misconfigured/unreachable
    webhook is logged and swallowed, never surfaced to the tenant's chat-completion request. See CLAUDE.md's
    "Quota alerting" section for the full design (anti-duplicate-per-window logic, highest-threshold-wins when
    multiple are crossed at once).

## Deployment

- Target: **Azure** first (App Service or Container Apps), using managed Postgres and managed Redis (Azure
  Cache for Redis). Containerize everything regardless, so a later move to Kubernetes (for cloud-agnostic or
  air-gapped/on-prem deployments) doesn't require re-architecting — just a different orchestration layer.

## Testing & CI

- **xUnit** for all .NET test projects.
- **GitHub Actions** runs build + test (and later lint) on PRs.
- `Dashboard` has no automated test suite yet (no Vitest/RTL/Playwright wired into `npm test` or CI) — it's
  been verified manually via a real headless-browser session (Playwright, driven ad hoc, not committed as a
  repeatable test) each time it's changed so far. Worth adding proper component/e2e coverage before the
  Dashboard grows much further; tracked as a gap, not a decision.

## Open questions / not yet decided

- Billing/invoicing integration (e.g. Stripe) if the gateway ever charges tenants a platform fee on top of BYOK.
- Email delivery for quota-threshold alerts — Phase 9 built webhook-only per explicit direction; email (or a
  second delivery mechanism generally) is deferred, not ruled out.
- When/whether to introduce schema- or database-per-tenant isolation for specific compliance-driven customers.
- Pooled/gateway-owned provider key support (future reseller tier).
- Production topology for `Dashboard` ↔ `Management`: local dev uses a same-origin Vite dev-server proxy (see
  CLAUDE.md), sidestepping CORS/cookie cross-origin rules entirely. A real deployment needs an equivalent
  same-origin arrangement (reverse proxy, or serve both from the same domain) or a properly configured
  `Cors:AllowedOrigins` + `SameSite=None; Secure` cross-origin cookie setup — not decided which, since it
  depends on the eventual hosting topology (Deployment section above is also not decided in enough detail yet).
- Which managed IdP to actually use (Entra ID external tenants vs. Auth0 vs. other) — blocked on having a real
  account to build/verify dynamic per-tenant client registration against; the JWT *validation* side is IdP-agnostic
  and already built (see AuthN/AuthZ above), but nothing has exercised real OIDC discovery against a live IdP.
- Per-tenant admin restriction on `Management`: today any valid JWT is trusted as a superadmin who can operate
  on every tenant (matches the existing fully-`Unscoped` DB trust model). A real multi-tenant admin story needs
  the IdP to carry a tenant/org claim for the logged-in admin and `Management` to check it against the tenant
  being operated on — not built, and deliberately not guessed at without knowing which IdP's claim conventions
  to design around.
