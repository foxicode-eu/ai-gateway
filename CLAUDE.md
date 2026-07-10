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

Phase 0 (solution scaffolding) and Phase 1 (core domain + persistence) are done — see `ROADMAP.md` for what's
next. There is still no auth, provider client, rate limiting, or streaming; don't assume that infrastructure
exists. Check `ROADMAP.md` before starting new work so you're building the current phase, not a later one out of
order, and update it as phases complete. Update `ARCHITECTURE.md` too if you make a decision that resolves one
of its "Open questions".

## Solution structure

`src/Gateway.slnx` (new `.slnx` format) references all projects below. All .NET projects target `net10.0` with
nullable reference types and implicit usings enabled.

- `src/Api/` — ASP.NET Core Web API (`Microsoft.NET.Sdk.Web`). Will become the data-plane proxy. Currently only
  has a stub endpoint: `GET /.well-known/ai-routing-configuration`. References `Core`, wired up to Postgres via
  `AddGatewayPersistence`.
- `src/Management/` — ASP.NET Core Web API (`Microsoft.NET.Sdk.Web`). Will become the control-plane API.
  Currently only has a stub `GET /healthz`. References `Core`, wired up to Postgres via `AddGatewayPersistence`.
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
- `src/Dashboard/` — React + TypeScript SPA (Vite), for tenant self-service. Currently the unmodified Vite
  starter template, not wired to any API yet.
- `src/Core.Tests/` — has real coverage of the tenant query-filter behavior (`Persistence/TenantScopeQueryFilterTests.cs`)
  using the EF Core InMemory provider. `src/Api.Tests/`, `src/Management.Tests/` are still empty — expected
  until those projects have logic to test.

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
