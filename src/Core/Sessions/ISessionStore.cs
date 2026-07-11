namespace Core.Sessions;

/// <summary>
/// Backs <c>Management</c>'s cookie-based admin sessions: the browser only ever holds an opaque session ID
/// (see <c>Management/Authentication/SessionCookies.cs</c>) — the actual authenticated identity lives here,
/// server-side, keyed by that ID. This is what makes it a real session rather than a JWT-in-a-cookie: stealing
/// the cookie value is useless without also compromising this store, and sessions can be revoked server-side
/// (logout) without needing token blocklisting.
/// </summary>
public interface ISessionStore
{
    Task SetAsync(string sessionId, string value, TimeSpan expiry, CancellationToken cancellationToken);

    /// <returns>The stored value, or null if the session doesn't exist (or has expired).</returns>
    Task<string?> GetAsync(string sessionId, CancellationToken cancellationToken);

    Task RemoveAsync(string sessionId, CancellationToken cancellationToken);
}
