using Api.Endpoints;
using Core.Persistence;
using Core.Providers;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Gateway")
    ?? throw new InvalidOperationException("Missing 'ConnectionStrings:Gateway' configuration.");
builder.Services.AddGatewayPersistence(connectionString);
builder.Services.AddOpenAiProviderClient(builder.Configuration);

var app = builder.Build();

app.MapGet("/.well-known/ai-routing-configuration", () =>
{
    return Results.Ok();
});

app.MapChatCompletions();

app.Run();

public partial class Program;
