using Api.Alerting;
using Api.Endpoints;
using Api.Observability;
using Api.RateLimiting;
using Core.Alerting;
using Core.Auth;
using Core.Observability;
using Core.Persistence;
using Core.Providers;
using Core.RateLimiting;
using Core.Secrets;
using Core.Security;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Gateway")
    ?? throw new InvalidOperationException("Missing 'ConnectionStrings:Gateway' configuration.");
builder.Services.AddGatewayPersistence(connectionString);
builder.Services.AddProviderClients(builder.Configuration);
builder.Services.AddGatewaySecrets(builder.Configuration);
builder.Services.AddApiKeyAuthentication();
builder.Services.AddManagedIdentityAuthentication(builder.Configuration);
builder.Services.AddGatewayRateLimiting(builder.Configuration);
builder.Services.AddScoped<RateLimitGate>();
builder.Services.AddGatewayAlerting();
builder.Services.AddScoped<QuotaAlertGate>();
builder.Services.AddScoped<UsageEventRecorder>();
builder.Services.AddGatewayObservability(builder.Configuration, serviceName: "ai-gateway-api");

var app = builder.Build();

app.MapGet("/.well-known/ai-routing-configuration", () =>
{
    return Results.Ok();
});

app.MapChatCompletions();

app.Run();

public partial class Program;
