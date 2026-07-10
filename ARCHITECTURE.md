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
- **`src/Dashboard`** — React + TypeScript SPA for tenant self-service (onboarding, key management, usage
  charts, quota alerts). Talks only to `src/Management`.
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
  sessions use standard authorization-code + PKCE flow.

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

- Enforced via Redis-backed sliding-window counters so limits are correct across multiple `Api` instances
  (required once the gateway scales beyond one replica).
- Granularity: quotas apply **per tenant and per API key** — a tenant gets an overall quota, and can also set
  finer-grained limits on individual API keys (e.g. to cap what one app/team within their org can consume).
- Flow: check remaining quota before proxying a request; after the provider responds (or the stream completes),
  record actual token usage against both the tenant and API-key counters.

## Observability & usage data

- Instrumented with **OpenTelemetry**, exporting traces/metrics via OTLP, so the backend (Azure Monitor,
  Grafana/Prometheus, Datadog, ...) can be swapped without re-instrumenting.
- Metrics tracked per tenant/API-key/provider/model: request count, latency, tokens in/out, error rate.
- **Payload privacy**: only request/response *metadata* is stored (token counts, latency, model, status code).
  Prompt and completion content is never persisted — this is a hard rule given the gateway handles arbitrary
  customer data across many tenants, not just a nice-to-have.
- Usage data feeds:
  - Per-tenant usage dashboards (via `Management` API, rendered in `Dashboard`).
  - Quota-threshold alerting (e.g. webhook/email when a tenant approaches its limit).

## Deployment

- Target: **Azure** first (App Service or Container Apps), using managed Postgres and managed Redis (Azure
  Cache for Redis). Containerize everything regardless, so a later move to Kubernetes (for cloud-agnostic or
  air-gapped/on-prem deployments) doesn't require re-architecting — just a different orchestration layer.

## Testing & CI

- **xUnit** for all .NET test projects.
- **GitHub Actions** runs build + test (and later lint) on PRs.

## Open questions / not yet decided

- Billing/invoicing integration (e.g. Stripe) if the gateway ever charges tenants a platform fee on top of BYOK.
- Concrete alerting delivery mechanism (webhook vs. email vs. both) for quota-threshold notifications.
- When/whether to introduce schema- or database-per-tenant isolation for specific compliance-driven customers.
- Pooled/gateway-owned provider key support (future reseller tier).
- Which managed IdP to actually use (Entra ID external tenants vs. Auth0 vs. other) — blocked on having a real
  account to build/verify dynamic per-tenant client registration against; the JWT *validation* side is IdP-agnostic
  and already built (see AuthN/AuthZ above), but nothing has exercised real OIDC discovery against a live IdP.
- Per-tenant admin restriction on `Management`: today any valid JWT is trusted as a superadmin who can operate
  on every tenant (matches the existing fully-`Unscoped` DB trust model). A real multi-tenant admin story needs
  the IdP to carry a tenant/org claim for the logged-in admin and `Management` to check it against the tenant
  being operated on — not built, and deliberately not guessed at without knowing which IdP's claim conventions
  to design around.
