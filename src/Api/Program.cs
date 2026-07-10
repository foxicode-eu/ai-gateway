var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/.well-known/ai-routing-configuration", () =>
{
    return Results.Ok();
});

app.Run();
