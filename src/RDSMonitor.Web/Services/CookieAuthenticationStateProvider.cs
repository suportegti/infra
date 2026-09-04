using Microsoft.AspNetCore.Components.Authorization;
using System.Security.Claims;

namespace RDSMonitor.Web.Services;

/// <summary>
/// Provider de estado de autenticação que lê o ClaimsPrincipal do HttpContext.
/// Quando o usuário faz login via POST no endpoint /api/auth/login, o cookie
/// de autenticação é gravado na resposta HTTP. Este provider o lê aqui para
/// informar ao Blazor qual usuário está logado.
/// </summary>
public class CookieAuthenticationStateProvider : AuthenticationStateProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CookieAuthenticationStateProvider(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var httpContext = _httpContextAccessor.HttpContext;
        var principal = httpContext?.User ?? new ClaimsPrincipal(new ClaimsIdentity());

        return Task.FromResult(new AuthenticationState(principal));
    }
}
