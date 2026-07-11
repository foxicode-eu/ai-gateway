using System.Security.Cryptography;
using Core.Sessions;
using Microsoft.Extensions.Options;

namespace Management.Authentication;

/// <summary>
/// Issues/validates/clears the admin session cookie. The cookie carries only an opaque session ID — see
/// <see cref="ISessionStore"/>'s doc comment for why that's the point.
/// </summary>
public sealed class SessionCookies(ISessionStore sessionStore, IOptions<GatewaySessionOptions> options)
{
    private GatewaySessionOptions Options => options.Value;

    public async Task<bool> IsValidAsync(HttpContext httpContext, CancellationToken cancellationToken)
    {
        var sessionId = httpContext.Request.Cookies[Options.CookieName];
        if (string.IsNullOrEmpty(sessionId))
        {
            return false;
        }

        return await sessionStore.GetAsync(sessionId, cancellationToken) is not null;
    }

    /// <param name="subject">An identifier for whoever authenticated — purely informational (logging/auditing),
    /// not used for authorization; Management trusts any valid session as a superadmin (see
    /// <see cref="AdminAuthenticationFilter"/>).</param>
    public async Task SignInAsync(HttpContext httpContext, string subject, CancellationToken cancellationToken)
    {
        var sessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var expiry = TimeSpan.FromMinutes(Options.IdleTimeoutMinutes);
        await sessionStore.SetAsync(sessionId, subject, expiry, cancellationToken);

        httpContext.Response.Cookies.Append(Options.CookieName, sessionId, new CookieOptions
        {
            HttpOnly = true,
            Secure = Options.CookieSecure,
            SameSite = SameSiteMode.Lax,
            Expires = DateTimeOffset.UtcNow.Add(expiry),
            Path = "/",
        });
    }

    public async Task SignOutAsync(HttpContext httpContext, CancellationToken cancellationToken)
    {
        var sessionId = httpContext.Request.Cookies[Options.CookieName];
        if (!string.IsNullOrEmpty(sessionId))
        {
            await sessionStore.RemoveAsync(sessionId, cancellationToken);
        }

        httpContext.Response.Cookies.Delete(Options.CookieName, new CookieOptions { Path = "/" });
    }
}
