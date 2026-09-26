using System.Security.Claims;
using Microsoft.Extensions.Caching.Memory;

namespace Goulash.Api.Auth;

public sealed class AdminSessionStore(IMemoryCache cache)
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(8);
    private const string SessionClaim = "admin_session_id";

    public ClaimsIdentity CreateIdentity()
    {
        var sessionId = Guid.NewGuid().ToString("N");
        cache.Set(sessionId, true, SessionLifetime);
        var identity = AdminPasswordVerifier.CreateIdentity();
        identity.AddClaim(new Claim(SessionClaim, sessionId));
        return identity;
    }

    public bool IsActive(ClaimsPrincipal principal)
    {
        var sessionId = principal.FindFirstValue(SessionClaim);
        return sessionId is not null && cache.TryGetValue(sessionId, out _);
    }

    public void Revoke(ClaimsPrincipal principal)
    {
        var sessionId = principal.FindFirstValue(SessionClaim);
        if (sessionId is not null)
            cache.Remove(sessionId);
    }
}
