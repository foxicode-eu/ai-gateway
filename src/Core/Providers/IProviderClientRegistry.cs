namespace Core.Providers;

public interface IProviderClientRegistry
{
    /// <exception cref="KeyNotFoundException">No provider is registered with that name.</exception>
    IProviderClient Get(string providerName);
}

public sealed class ProviderClientRegistry : IProviderClientRegistry
{
    private readonly Dictionary<string, IProviderClient> _clientsByName;

    public ProviderClientRegistry(IEnumerable<IProviderClient> clients)
    {
        _clientsByName = clients.ToDictionary(c => c.ProviderName, StringComparer.OrdinalIgnoreCase);
    }

    public IProviderClient Get(string providerName)
    {
        if (!_clientsByName.TryGetValue(providerName, out var client))
        {
            throw new KeyNotFoundException($"No provider client registered for '{providerName}'.");
        }

        return client;
    }
}
