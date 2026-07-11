using Core.Auth;
using Core.Observability;
using Core.Persistence;
using Core.Providers;
using Core.Secrets;
using Core.Sessions;
using Core.Tenancy;
using Management.Authentication;
using Management.Endpoints;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Gateway")
    ?? throw new InvalidOperationException("Missing 'ConnectionStrings:Gateway' configuration.");
builder.Services.AddGatewayPersistence(connectionString);
builder.Services.AddProviderClients(builder.Configuration);
builder.Services.AddGatewaySecrets(builder.Configuration);
builder.Services.AddManagedIdentityAuthentication(builder.Configuration);
builder.Services.AddGatewayObservability(builder.Configuration, serviceName: "ai-gateway-management");
builder.Services.AddGatewaySessions(builder.Configuration);
builder.Services.AddScoped<SessionCookies>();

// No cross-origin browser access by default (empty allowed-origins = CORS rejects it). Only needed if the
// Dashboard is ever served from a different origin than Management without a same-origin dev proxy in front —
// see CLAUDE.md's Dashboard section for why local dev doesn't need this at all (Vite proxies same-origin).
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(cors =>
{
    cors.AddPolicy("Dashboard", policy => policy
        .WithOrigins(allowedOrigins)
        .AllowCredentials()
        .AllowAnyHeader()
        .AllowAnyMethod());
});

var app = builder.Build();

app.UseCors("Dashboard");

app.MapGet("/healthz", () => Results.Ok());

// Management is the trusted control plane — every request here operates across tenants (tenant CRUD, API key
// issuance, etc.), so it runs Unscoped rather than resolving a single-tenant scope per request the way the
// data-plane Api does.
app.Use(async (context, next) =>
{
    context.RequestServices.GetRequiredService<ICurrentTenantAccessor>().SetScope(TenantScope.Unscoped);
    await next(context);
});

app.MapAuth();

var tenantsGroup = app.MapGroup("/tenants").AddEndpointFilter<AdminAuthenticationFilter>();
tenantsGroup.MapTenants();
tenantsGroup.MapApiKeys();
tenantsGroup.MapProviderCredentials();
tenantsGroup.MapUsage();

app.Run();

public partial class Program;
