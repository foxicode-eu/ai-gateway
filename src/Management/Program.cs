using Core.Persistence;
using Core.Providers;
using Core.Secrets;
using Core.Tenancy;
using Management.Endpoints;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Gateway")
    ?? throw new InvalidOperationException("Missing 'ConnectionStrings:Gateway' configuration.");
builder.Services.AddGatewayPersistence(connectionString);
builder.Services.AddProviderClients(builder.Configuration);
builder.Services.AddGatewaySecrets(builder.Configuration);

var app = builder.Build();

app.MapGet("/healthz", () => Results.Ok());

// Management is the trusted control plane — every request here operates across tenants (tenant CRUD, API key
// issuance, etc.), so it runs Unscoped rather than resolving a single-tenant scope per request the way the
// data-plane Api does.
app.Use(async (context, next) =>
{
    context.RequestServices.GetRequiredService<ICurrentTenantAccessor>().SetScope(TenantScope.Unscoped);
    await next(context);
});

app.MapTenants();
app.MapApiKeys();
app.MapProviderCredentials();

app.Run();

public partial class Program;
