namespace Api.Authentication;

/// <summary>
/// Who <see cref="TenantAuthenticationFilter"/> resolved the request to. Stashed in
/// <c>HttpContext.Items[nameof(AuthenticatedTenant)]</c> so downstream handlers (e.g. rate limiting) don't need
/// to re-derive it. <see cref="ApiKeyId"/> is only set for the legacy API-key auth path — JWT-authenticated
/// requests have no notion of "which key" since that credential model doesn't have one (see
/// <see cref="TenantAuthenticationFilter"/>'s doc comment).
/// </summary>
public sealed record AuthenticatedTenant(Guid TenantId, Guid? ApiKeyId);
