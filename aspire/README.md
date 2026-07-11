# Local orchestration with .NET Aspire + YARP

A single `dotnet run` that starts Postgres, Redis, `Api`, `Management`, and the Dashboard's Vite dev server,
applies EF Core migrations, and fronts everything behind one YARP gateway URL — so you can exercise the whole
gateway locally (tenant onboarding → BYOK → chat completion → Dashboard) without juggling five terminals or
memorizing which port is which. This is a local-dev/testing convenience, separate from and complementary to:

- `docker-compose.yml` + `dotnet run --project src/Api` / `src/Management` / `npm run dev` — the original
  per-service local dev workflow (still fully supported, unchanged, documented in the repo root `CLAUDE.md`).
- `docker-compose.full.yml` + `deploy/` — the Phase 10 deployment-artifact verification path (Dockerfiles, Azure
  Bicep/CD). This AppHost doesn't touch either.

Not part of `src/Gateway.slnx` on purpose — it's an optional layer with its own (fairly heavy) package set, not
something `dotnet build src/Gateway.slnx`/CI should need to restore.

## Run it

```bash
cd aspire/AppHost
ASPIRE_ALLOW_UNSECURED_TRANSPORT=true dotnet run --launch-profile http
```

Then open **`http://localhost:5100`** — that's the one URL. It redirects to `/login`; paste a JWT minted the
same way as every other local-dev walkthrough in this repo:

```bash
dotnet run --project src/DevTools -- mint-token 00000000-0000-0000-0000-000000000000
```

From there: create a tenant, set a provider credential, and either use the Dashboard UI or call
`http://localhost:5100/v1/chat/completions` directly with a data-plane token — same as the existing
`curl` walkthrough in the repo root `CLAUDE.md`, just against port 5100 instead of 5116/5162 separately.

**Why `--launch-profile http` + `ASPIRE_ALLOW_UNSECURED_TRANSPORT=true`:** the default (`https`) launch profile
requires a *trusted* local dev HTTPS certificate for the Aspire dashboard's own gRPC telemetry link
(`dotnet dev-certs https --trust`), which needs an interactive OS keychain prompt — not available in every
environment (headless CI, some sandboxes) and not worth the friction for local smoke-testing. Plain HTTP for the
AppHost's own dashboard sidesteps it entirely; the actual `Api`/`Management`/`Dashboard`/YARP resources are
unaffected either way. If you *do* have a trusted dev cert already, the default `dotnet run` (no flags) works
fine too.

**Every run starts from a fresh, empty database** (see "Why no persistent Postgres volume" below) — migrations
re-apply automatically each time, but any tenants/data you created in a previous run are gone. If you want data
to survive across runs, use the `docker-compose.yml` + `dotnet run` workflow instead, which does persist.

## Routing scheme

| Path | Destination | Notes |
|---|---|---|
| `/v1/**` | `Api` | Unprefixed passthrough — an OpenAI SDK client can't be told to add a path prefix, so `Api`'s public contract (`/v1/chat/completions`, etc.) has to reach YARP byte-for-byte unchanged. |
| `/.well-known/**` | `Api` | Same reasoning — the stub routing-configuration endpoint. |
| `/api/**` | `Management` | Prefix stripped before forwarding (`WithTransformPathRemovePrefix`). Matches the convention `src/Dashboard/src/lib/api.ts` and `src/Dashboard/nginx.conf.template` already use, so the Dashboard's own code needed zero changes to work behind this gateway. |
| everything else | Dashboard (Vite dev server) | SPA assets, HMR websocket, client-side routes. |

ASP.NET Core's routing engine prioritizes more specific literal-segment routes over a catch-all regardless of
registration order, so `/{**catch-all}` being registered last doesn't matter — `/v1/**` and `/api/**` always win
first for their prefixes.

## What's wired up, and why

- **Postgres + Redis**: `builder.AddPostgres("postgres")` / `.AddDatabase("Gateway")` (the database resource is
  named `"Gateway"` specifically to match the `ConnectionStrings:Gateway` config key `Api`/`Management` already
  read — Aspire auto-injects `ConnectionStrings__Gateway` as an env var via `.WithReference(gatewayDb)`, no code
  changes needed) and `builder.AddRedis("redis")`. `RateLimiting__RedisConnectionString` /
  `Sessions__RedisConnectionString` are mapped explicitly via `.WithEnvironment(..., redis.Resource.ConnectionStringExpression)`
  since those config keys don't follow the `ConnectionStrings:*` convention Aspire auto-wires.
- **Migrations**: an `AddExecutable` resource running `dotnet ef database update` against `src/Core` (the exact
  command the repo-root `CLAUDE.md` already documents running by hand), gated on Postgres via `.WaitFor(gatewayDb)`.
  `Api`/`Management` both `.WaitForCompletion(migrations)` before starting.
- **`Api` / `Management`**: `AddProject<Projects.Api>`/`AddProject<Projects.Management>` — typed references,
  which is why `AppHost.csproj` has `<ProjectReference>`s to their `.csproj` files (that's what makes the
  source-generated `Projects.Api`/`Projects.Management` types exist). Both explicitly use `launchProfileName:
  "http"` so they come up on the same ports (`5116`/`5162`) documented everywhere else in this repo, and so each
  has exactly one endpoint (no ambiguous http+https pair) for YARP to resolve.
- **Dashboard**: `AddNpmApp("dashboard", "../../src/Dashboard", "dev")` — literally runs `npm run dev` (Vite) as
  a child process. Hot reload works exactly like running it by hand.
- **YARP**: `AddYarp("proxy")` (not `"gateway"` — see the comment in `AppHost.cs`; that name collides
  case-insensitively with the `"Gateway"` database resource) with routes built from direct resource-builder
  references (`yarp.AddRoute(path, resourceBuilder)`), so destinations resolve through Aspire's own service
  discovery — no hardcoded ports anywhere in the routing config itself.

## Two real bugs found by actually running this (not just building it)

Both would have shipped invisibly if this had only been checked with `dotnet build` + a single `curl /healthz`:

1. **Vite's React Fast Refresh silently disabled.** Aspire's NodeJs hosting integration defaults child processes
   to `NODE_ENV=production`. `@vitejs/plugin-react` uses that to decide whether to inject its Fast Refresh
   preamble script — with it unset to `development`, the page loads (200 OK, real HTML) but every component
   module throws `ReferenceError: $RefreshSig$ is not defined` the instant it evaluates, breaking the entire app
   silently from `curl`'s perspective (still 200) but fatally from a real browser's. Only surfaced by actually
   driving a headless browser against it and reading `pageerror` events — a `curl | grep 200` check would have
   passed every time. Fixed with an explicit `.WithEnvironment("NODE_ENV", "development")` on the dashboard
   resource.
2. **Persistent Postgres volume + per-run generated password = works once, hangs forever on the second run.**
   Aspire generates a fresh random Postgres password each AppHost run (no explicit credentials configured), but
   `.WithDataVolume()` keeps the *previous* run's Postgres data directory around — and Postgres only applies
   `POSTGRES_PASSWORD` at first-time `initdb`, not on every container start. Second run: new random password,
   old data directory, migrations job can't authenticate, and every resource waiting on it
   (`WaitForCompletion(migrations)`) hangs indefinitely with no obvious error in the AppHost's own console output
   (the failure is inside the `migrations` executable resource's own log stream). Only caught by actually running
   the AppHost twice in a row, not once. Fixed by dropping the persistent volume entirely — see the code comment
   on why that's actually the right trade-off here, not just a workaround.

## Known limitations

- Aspire's own dashboard UI (telemetry/resource inspector) wasn't dug into further than confirming it loads —
  the `https` launch profile's dev-cert-trust friction pushed this session toward `http`, where the dashboard
  still works but wasn't the focus. If you want OTLP traces flowing into it from `Api`/`Management`, you'd need
  to point `Observability:Exporter`/`Observability:OtlpEndpoint` at the AppHost's OTLP endpoint (printed in its
  startup log) — not wired up here, since `Observability:Exporter=Console` (the existing local-dev default)
  already satisfies CLAUDE.md's local-dev observability story.
- No automated test/CI coverage for this orchestration layer itself (it's shell-verified, per this document) —
  consistent with how `docker-compose.full.yml` and `deploy/` are treated in Phase 10.
