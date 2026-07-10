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

Phase 0 (solution scaffolding) is done: all projects described in `ARCHITECTURE.md` exist and build, but contain
no business logic yet — no tenant model, auth, provider client, rate limiting, or persistence layer. Don't assume
supporting infrastructure already exists beyond what's listed below; check `ROADMAP.md` for what phase is next
and update it as phases complete. Update `ARCHITECTURE.md` too if you make a decision that resolves one of its
"Open questions".

## Solution structure

`src/Gateway.slnx` (new `.slnx` format) references all projects below. All .NET projects target `net10.0` with
nullable reference types and implicit usings enabled.

- `src/Api/` — ASP.NET Core Web API (`Microsoft.NET.Sdk.Web`). Will become the data-plane proxy. Currently only
  has a stub endpoint: `GET /.well-known/ai-routing-configuration`. References `Core`.
- `src/Management/` — ASP.NET Core Web API (`Microsoft.NET.Sdk.Web`). Will become the control-plane API.
  Currently only has a stub `GET /healthz`. References `Core`.
- `src/Core/` — class library for shared domain code (tenant/domain model, provider client abstractions,
  rate-limit primitives) used by both `Api` and `Management`. Currently empty.
- `src/Dashboard/` — React + TypeScript SPA (Vite), for tenant self-service. Currently the unmodified Vite
  starter template, not wired to any API yet.
- `src/Core.Tests/`, `src/Api.Tests/`, `src/Management.Tests/` — xUnit test projects, one per matching
  non-test project. Currently empty (no test files) — this is expected until each project has logic to test.

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

CI (`.github/workflows/ci.yml`) runs `dotnet build`/`dotnet test` on the solution and `npm run build` on the
Dashboard for every PR.
