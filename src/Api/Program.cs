using Api.Endpoints;
using Core.Auth;
using Core.Persistence;
using Core.Providers;
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

var app = builder.Build();

app.MapGet("/.well-known/ai-routing-configuration", () =>
{
    return Results.Ok();
});

app.MapChatCompletions();

app.Run();

public partial class Program;
