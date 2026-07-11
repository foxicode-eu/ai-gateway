using System.Net;
using System.Net.Http.Json;
using Core.Auth;
using Management.Endpoints;
using Xunit;

namespace Management.Tests;

public class AuthEndpointTests : IClassFixture<ManagementApiFactory>
{
    private readonly ManagementApiFactory _factory;

    public AuthEndpointTests(ManagementApiFactory factory)
    {
        _factory = factory;
    }

    private string MintToken() => LocalDevTokenIssuer.IssueToken(_factory.AuthenticationOptions, Guid.NewGuid());

    // WebApplicationFactory's test client has no automatic cookie jar (it's not a real HttpClientHandler with a
    // CookieContainer — TestServer uses its own in-memory transport), so tests propagate Set-Cookie manually,
    // the same way any non-browser HTTP client integrating with this API would have to.
    private static string ExtractSessionCookie(HttpResponseMessage response)
    {
        var setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        return setCookie.Split(';')[0]; // "ai_gateway_session=<value>", dropping HttpOnly/Path/etc. attributes
    }

    [Fact]
    public async Task Login_with_a_valid_token_sets_a_session_cookie_that_authenticates_subsequent_requests()
    {
        var client = _factory.CreateClient();

        var loginResponse = await client.PostAsJsonAsync("/auth/login", new AuthEndpoint.LoginRequest(MintToken()));
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var cookie = ExtractSessionCookie(loginResponse);

        var request = new HttpRequestMessage(HttpMethod.Post, "/tenants")
        {
            Content = JsonContent.Create(new TenantsEndpoint.CreateTenantRequest("Acme")),
        };
        request.Headers.Add("Cookie", cookie);
        var tenantsResponse = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, tenantsResponse.StatusCode);
    }

    [Fact]
    public async Task Login_with_an_invalid_token_is_rejected_and_sets_no_cookie()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/login", new AuthEndpoint.LoginRequest("not-a-valid-token"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Logout_invalidates_the_session()
    {
        var client = _factory.CreateClient();
        var loginResponse = await client.PostAsJsonAsync("/auth/login", new AuthEndpoint.LoginRequest(MintToken()));
        var cookie = ExtractSessionCookie(loginResponse);

        var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/auth/logout");
        logoutRequest.Headers.Add("Cookie", cookie);
        var logoutResponse = await client.SendAsync(logoutRequest);
        Assert.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);

        var afterLogoutRequest = new HttpRequestMessage(HttpMethod.Post, "/tenants")
        {
            Content = JsonContent.Create(new TenantsEndpoint.CreateTenantRequest("Acme")),
        };
        afterLogoutRequest.Headers.Add("Cookie", cookie);
        var afterLogoutResponse = await client.SendAsync(afterLogoutRequest);

        Assert.Equal(HttpStatusCode.Unauthorized, afterLogoutResponse.StatusCode);
    }

    [Fact]
    public async Task GetSession_reflects_current_login_state()
    {
        var client = _factory.CreateClient();

        var before = await client.GetAsync("/auth/session");
        Assert.Equal(HttpStatusCode.Unauthorized, before.StatusCode);

        var loginResponse = await client.PostAsJsonAsync("/auth/login", new AuthEndpoint.LoginRequest(MintToken()));
        var cookie = ExtractSessionCookie(loginResponse);

        var afterRequest = new HttpRequestMessage(HttpMethod.Get, "/auth/session");
        afterRequest.Headers.Add("Cookie", cookie);
        var after = await client.SendAsync(afterRequest);

        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task A_bearer_token_still_works_without_a_session_cookie()
    {
        var client = _factory.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/tenants", new TenantsEndpoint.CreateTenantRequest("Acme"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}
