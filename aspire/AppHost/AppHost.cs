// Local-dev orchestrator: one `dotnet run` starts Postgres, Redis, Api, Management, and the Dashboard's Vite
// dev server, fronts them all behind a single YARP gateway URL, and applies EF Core migrations automatically.
// See aspire/README.md for the full picture (routing scheme, port numbers, what's deliberately NOT covered).

using Aspire.Hosting.Yarp.Transforms;

var builder = DistributedApplication.CreateBuilder(args);

// Deliberately no `.WithDataVolume()` — Aspire generates a fresh random Postgres password every AppHost run
// (see UserSecretsId above), but a *persistent* data volume keeps the previous run's password baked into
// Postgres's data directory (only applied at initdb time). The two combined means the second run's Postgres
// container silently fails to authenticate its own migrations job, which then hangs everything waiting on it
// forever — caught by actually running this twice, not just once. An ephemeral per-run database is the right
// trade-off here anyway: this AppHost is for local smoke-testing, not long-lived dev data (that's what
// docker-compose.yml + `dotnet run --project src/Api` is for — see aspire/README.md).
var postgres = builder.AddPostgres("postgres");
var gatewayDb = postgres.AddDatabase("Gateway");

var redis = builder.AddRedis("redis");

// Applies EF Core migrations before Api/Management start — same `dotnet ef database update` command
// CLAUDE.md's local-dev walkthrough already documents, just run automatically here instead of by hand.
var migrations = builder.AddExecutable(
        "migrations",
        "dotnet",
        "../../src/Core",
        ["ef", "database", "update", "--project", ".", "--startup-project", "."])
    .WithReference(gatewayDb)
    .WaitFor(gatewayDb)
    .WithEnvironment("GATEWAY_DB_CONNECTION_STRING", gatewayDb.Resource.ConnectionStringExpression);

// Both projects use their "http" launchProfile explicitly (rather than Aspire's default profile pick) so the
// ports match what CLAUDE.md already documents (5116/5162) whether you start them via `dotnet run` directly or
// through this AppHost, and so each resource has exactly one endpoint (no separate https endpoint to disambiguate
// when YARP resolves a destination for it).
var api = builder.AddProject<Projects.Api>("api", launchProfileName: "http")
    .WithReference(gatewayDb)
    .WaitForCompletion(migrations)
    .WithEnvironment("RateLimiting__Store", "Redis")
    .WithEnvironment("RateLimiting__RedisConnectionString", redis.Resource.ConnectionStringExpression)
    .WaitFor(redis);

var management = builder.AddProject<Projects.Management>("management", launchProfileName: "http")
    .WithReference(gatewayDb)
    .WaitForCompletion(migrations)
    .WithEnvironment("Sessions__Store", "Redis")
    .WithEnvironment("Sessions__RedisConnectionString", redis.Resource.ConnectionStringExpression)
    .WaitFor(redis);

// Runs the existing Vite dev server (`npm run dev`) as a plain child process — hot reload still works exactly
// like running it by hand, this just gives it a place in the unified URL / dependency graph.
//
// NODE_ENV override is load-bearing, not cosmetic: Aspire's NodeJs hosting integration defaults child
// processes to NODE_ENV=production, which silently disables @vitejs/plugin-react's React Fast Refresh preamble
// injection — the page loads but every component module throws `ReferenceError: $RefreshSig$ is not defined`
// the instant it evaluates, breaking the app outright. Caught by actually driving a browser against it, not by
// `curl`ing for a 200. Vite itself still runs in dev/serve mode either way; this only fixes the plugin's
// production-vs-development check.
var dashboard = builder.AddNpmApp("dashboard", "../../src/Dashboard", "dev")
    .WithHttpEndpoint(targetPort: 5173, env: "PORT")
    .WithEnvironment("NODE_ENV", "development")
    .WithReference(management)
    .WaitFor(management);

// The one URL: http://localhost:5100. /v1/** and /.well-known/** keep Api's OpenAI-compatible paths byte-for-byte
// (no prefix — an actual OpenAI SDK client can't be told to add one), /api/** strips its prefix and forwards to
// Management (matching the convention `src/Dashboard/src/lib/api.ts` and `nginx.conf.template` already use), and
// everything else falls through to the Dashboard SPA/Vite assets/HMR websocket. ASP.NET Core's routing prioritizes
// the more specific literal-segment routes over the catch-all regardless of registration order, so no explicit
// route ordering is needed here.
// Named "proxy", not "gateway" — the Postgres *database* resource is already named "Gateway" (has to match the
// `ConnectionStrings:Gateway` config key the apps read), and Aspire resource names are case-insensitive, so
// "gateway" collides with it.
builder.AddYarp("proxy")
    .WithHostPort(5100)
    .WithConfiguration(yarp =>
    {
        yarp.AddRoute("/v1/{**catch-all}", api);
        yarp.AddRoute("/.well-known/{**catch-all}", api);
        yarp.AddRoute("/api/{**catch-all}", management).WithTransformPathRemovePrefix("/api");
        yarp.AddRoute("/{**catch-all}", dashboard);
    });

builder.Build().Run();
