using Core.Persistence;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Gateway")
    ?? throw new InvalidOperationException("Missing 'ConnectionStrings:Gateway' configuration.");
builder.Services.AddGatewayPersistence(connectionString);

var app = builder.Build();

app.MapGet("/.well-known/ai-routing-configuration", () =>
{
    return Results.Ok();
});

app.Run();
