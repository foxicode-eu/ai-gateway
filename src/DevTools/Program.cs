using Core.Auth;
using Microsoft.Extensions.Configuration;

// Local development only: mints JWTs signed with the "StaticKey" configuration shared by src/Api and
// src/Management's appsettings.Development.json, for manually testing authenticated requests with curl. The
// running gateway processes never issue tokens themselves — see LocalDevTokenIssuer's doc comment.
//
// Usage: dotnet run --project src/DevTools -- mint-token <tenant-id-guid> [--config <path-to-appsettings.json>]

if (args.Length < 2 || args[0] != "mint-token")
{
    Console.Error.WriteLine("Usage: dotnet run --project src/DevTools -- mint-token <tenant-id-guid> [--config <path>]");
    return 1;
}

if (!Guid.TryParse(args[1], out var tenantId))
{
    Console.Error.WriteLine($"'{args[1]}' is not a valid GUID.");
    return 1;
}

var configPathIndex = Array.IndexOf(args, "--config");
var configPath = configPathIndex >= 0 && configPathIndex + 1 < args.Length
    ? args[configPathIndex + 1]
    : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Api", "appsettings.Development.json");

var configuration = new ConfigurationBuilder()
    .AddJsonFile(Path.GetFullPath(configPath), optional: false)
    .Build();

var options = configuration.GetSection(AuthenticationOptions.ConfigurationSection).Get<AuthenticationOptions>()
    ?? throw new InvalidOperationException($"No '{AuthenticationOptions.ConfigurationSection}' section found in {configPath}.");

var token = LocalDevTokenIssuer.IssueToken(options, tenantId);
Console.WriteLine(token);
return 0;
